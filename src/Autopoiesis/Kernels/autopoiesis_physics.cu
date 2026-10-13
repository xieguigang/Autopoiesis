// ---------------------------------------------------------------------------
// autopoiesis_physics.cu —— 粒子生命（Particle Life）的 O(N^2) 粒子对力内核
//
// 本文件是 algorithm/compute.glsl 中 apply_physics() 的第一遍（核密度估计）
// 与第二遍（分段线性力场）在 CUDA 上的等价实现，**逐行保持语义一致**：
//
//   * 最小像约定   delta -= round(delta / region) * region   （x、y 分量各自独立）
//   * 密度核       k(d) = 1 - d / interaction_radius，同类型权重 1、异类型 0.5
//   * 密度抑制     phi = 1 - clamp(rho - density_limit, 0, 1.005)，仅作用于 A > 0
//   * 力廓形       d < r  -> 2(d/r - 1)
//                  else  -> A' * (1 - |d - m| / (W/2))，m = (r + R)/2，W = R - r
//   * 矢量叠加     F_i = sum_j n_ij * f(d_ij) * (-50)，n_ij = delta / |delta|
//   * 退化抖动     d == 0 时注入确定性 sin-hash 抖动（对称性破缺，避免永久锁死）
//
// 与 GLSL 原版的分工差异：力的截断（|F| > 100 时归一化）与时间积分、环面回绕
// 都**不**在这里做，而是交给 TensorF 单精度张量算子完成
// （见 Physics/TensorIntegrator.vb）。因此本内核的输出是"未限幅的合力"。
//
// 编译约束（ILCuda 的 NvrtcCompiler 只传 -arch=，没有 -I / --std / fast-math，
// 且所有 *.cu 会被拼进同一个编译单元）：
//   * 必须是 extern "C" __global__ 裸函数（内核按未修饰的 C 名查找）
//   * 禁止 #include，只使用 NVRTC 内建的 device 数学函数
//   * 符号名统一加 autopoiesis 前缀，避免与 ILCuda / ILCudaTensor 的
//     ewAddKernel / tensorF32* 等同名函数撞车导致整个编译单元失败
//   * 必须写 grid-stride 循环：LaunchPlanner.For1D 会把 block 数静默截断到 4096
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// 确定性伪随机抖动（GLSL sin-hash 的逐字复刻）
//
//   float random(float v) {
//       return fract(sin(dot(vec2(v, v / 10.0), vec2(12.9898, 78.233))) * 43758.5453);
//   }
//
// dot(vec2(v, v/10), vec2(12.9898, 78.233)) == v * (12.9898 + 7.8233)
// ---------------------------------------------------------------------------
__device__ __forceinline__ float autopoiesisRandom(float v)
{
    float t = sinf(v * 20.8131f) * 43758.5453f;
    return t - floorf(t);
}

extern "C" __global__ void autopoiesisPairForceF32(
    const float* __restrict__ posX,       // [n] 位置 x
    const float* __restrict__ posY,       // [n] 位置 y
    const int*   __restrict__ types,      // [n] 粒子类型，取值 [0, nTypes)
    const float* __restrict__ forceMat,   // [forceStride^2] 交互矩阵，索引 t_a + t_b * forceStride
    float*       __restrict__ forceX,     // [n] 出参：未限幅合力 x
    float*       __restrict__ forceY,     // [n] 出参：未限幅合力 y
    float*       __restrict__ density,    // [n] 出参：核密度估计 rho
    const int   n,
    const int   forceStride,
    const float regionW,
    const float regionH,
    const float repulsionRadius,
    const float interactionRadius,
    const float densityLimit)
{
    if (n <= 0) {
        return;
    }

    const float maxRadiusSq = interactionRadius * interactionRadius;

    for (int i = blockIdx.x * blockDim.x + threadIdx.x; i < n; i += blockDim.x * gridDim.x) {
        const float posAX = posX[i];
        const float posAY = posY[i];
        const int typeA = types[i];

        // ------------------------------------------------------------------
        // 第一遍：核密度估计 rho_i
        //
        // 必须独立成一遍：若与力求值合并，早处理的邻居与晚处理的邻居会看到
        // 不同的 rho_i，更新就不再是"对旧状态的纯函数"。
        // ------------------------------------------------------------------
        float localDensity = 0.0f;

        for (int j = 0; j < n; ++j) {
            if (j == i) {
                continue;
            }

            float dx = posAX - posX[j];
            float dy = posAY - posY[j];

            dx -= roundf(dx / regionW) * regionW;
            dy -= roundf(dy / regionH) * regionH;

            const float distSq = dx * dx + dy * dy;

            if (distSq > maxRadiusSq) {
                continue;
            }

            const float dist = sqrtf(distSq);

            if (dist > 0.0f) {
                if (types[j] == typeA) {
                    localDensity += 1.0f - dist / interactionRadius;
                } else {
                    localDensity += (1.0f - dist / interactionRadius) * 0.5f;
                }
            }
        }

        // ------------------------------------------------------------------
        // 第二遍：逐对力矢量叠加
        // ------------------------------------------------------------------
        float totalForceX = 0.0f;
        float totalForceY = 0.0f;

        const float width = interactionRadius - repulsionRadius;
        const float midpoint = (repulsionRadius + interactionRadius) * 0.5f;
        const float halfWidth = width * 0.5f;

        for (int j = 0; j < n; ++j) {
            if (j == i) {
                continue;
            }

            float dx = posAX - posX[j];
            float dy = posAY - posY[j];

            dx -= roundf(dx / regionW) * regionW;
            dy -= roundf(dy / regionH) * regionH;

            const float distSq = dx * dx + dy * dy;

            if (distSq > maxRadiusSq) {
                continue;
            }

            const float dist = sqrtf(distSq);

            if (dist > 0.0f) {
                const int typeB = types[j];
                float attraction = forceMat[typeA + typeB * forceStride];

                // 密度抑制只作用于吸引部分：拥挤到超过上限后吸引力线性衰减，
                // 极端拥挤时 phi 甚至可以略微为负（轻微排斥）。
                if (attraction > 0.0f) {
                    float over = localDensity - densityLimit;

                    if (over < 0.0f) {
                        over = 0.0f;
                    } else if (over > 1.005f) {
                        over = 1.005f;
                    }

                    attraction *= 1.0f - over;
                }

                float force;

                if (dist < repulsionRadius) {
                    // 短程硬核排斥：恒为负，斜率 2/r
                    force = (dist / repulsionRadius - 1.0f) * 2.0f;
                } else {
                    // 中程三角形吸引核：峰值 attraction，两端为零
                    force = attraction * (1.0f - fabsf(dist - midpoint) / halfWidth);
                }

                // n_ij 从邻居 j 指向 i
                const float invDist = 1.0f / dist;
                const float scale = force * -50.0f;

                totalForceX += dx * invDist * scale;
                totalForceY += dy * invDist * scale;
            } else {
                // 完全重合：对称性破缺用的确定性抖动（互不相关）
                totalForceX += autopoiesisRandom((float)i + posAX) - 0.5f;
                totalForceY += autopoiesisRandom((float)i + posAY) - 0.5f;
            }
        }

        forceX[i] = totalForceX;
        forceY[i] = totalForceY;
        density[i] = localDensity;
    }
}
