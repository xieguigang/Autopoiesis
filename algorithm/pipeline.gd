class_name Pipeline
extends Node

const GROUP_DENSITY = 256

var rd: RenderingDevice
var shader_file := preload("res://scenes/simulation/compute.glsl") as RDShaderFile
var shader_spirv: RDShaderSPIRV
var shader: RID
var pipeline: RID

# Uniform sets
var uniform_set_a: RID
var uniform_set_b: RID
var tick: int = 0

# Buffers
var pos_buffer_a: RID
var pos_buffer_b: RID
var vel_buffer_a: RID
var vel_buffer_b: RID
var type_buffer: RID
var force_buffer: RID
var color_buffer: RID

# Texture
var texture: Texture2DRD
var texture_buffer: RID
var texture_format: RDTextureFormat
var texture_view: RDTextureView

@onready var simulation := $".."
@onready var particles := %Particles
@onready var region := %Region


func _ready() -> void:
	rd = RenderingServer.get_rendering_device()
	shader_spirv = shader_file.get_spirv()
	shader = rd.shader_create_from_spirv(shader_spirv)
	pipeline = rd.compute_pipeline_create(shader)

	texture = Texture2DRD.new()
	texture_format = RDTextureFormat.new()
	texture_format.width = simulation.REGION_SIZE.x
	texture_format.height = simulation.REGION_SIZE.y
	texture_format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT
	texture_format.usage_bits = (
		RenderingDevice.TEXTURE_USAGE_STORAGE_BIT
		| RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
		| RenderingDevice.TEXTURE_USAGE_CPU_READ_BIT
		| RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		| RenderingDevice.TEXTURE_USAGE_CAN_COPY_TO_BIT
		| RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	)
	texture_view = RDTextureView.new()

	var texture_source := Image.create(
		simulation.REGION_SIZE.x, simulation.REGION_SIZE.y, false, Image.FORMAT_RGBAF
	)
	texture_buffer = rd.texture_create(texture_format, texture_view, [texture_source.get_data()])
	texture.texture_rd_rid = texture_buffer


func _process(delta: float) -> void:
	delta *= 5.0

	if simulation.is_active:
		var uniform_set := uniform_set_a if tick % 2 == 0 else uniform_set_b

		var forces_bytes: PackedByteArray = particles.forces.to_byte_array()
		rd.buffer_update(force_buffer, 0, forces_bytes.size(), forces_bytes)

		var colors_bytes: PackedByteArray = particles.colors.to_byte_array()
		rd.buffer_update(color_buffer, 0, colors_bytes.size(), colors_bytes)

		run_compute_shader(uniform_set, 0, delta)
		rd.texture_clear(texture_buffer, Color(0.0, 0.0, 0.0, 1.0), 0, 1, 0, 1)  #Color(0.123, 0.126, 0.153, 1.0)
		run_compute_shader(uniform_set, 1, delta)

		region.texture = texture
		tick += 1


func _exit_tree() -> void:
	var rids: Array[RID] = [
		shader,
		pipeline,
		uniform_set_a,
		uniform_set_b,
		pos_buffer_a,
		pos_buffer_b,
		vel_buffer_a,
		vel_buffer_b,
		type_buffer,
		force_buffer,
		color_buffer,
		texture_buffer
	]

	for rid in rids:
		if rid.is_valid():
			rd.free_rid(rid)


func create_buffers() -> void:
	var position_bytes: PackedByteArray = particles.positions.to_byte_array()
	var velocity_bytes: PackedByteArray = particles.velocities.to_byte_array()
	var type_bytes: PackedByteArray = particles.types.to_byte_array()
	var forces_bytes: PackedByteArray = particles.forces.to_byte_array()
	var color_bytes: PackedByteArray = particles.colors.to_byte_array()

	pos_buffer_a = rd.storage_buffer_create(position_bytes.size(), position_bytes)
	pos_buffer_b = rd.storage_buffer_create(position_bytes.size(), position_bytes)
	vel_buffer_a = rd.storage_buffer_create(velocity_bytes.size(), velocity_bytes)
	vel_buffer_b = rd.storage_buffer_create(velocity_bytes.size(), velocity_bytes)

	type_buffer = rd.storage_buffer_create(type_bytes.size(), type_bytes)
	force_buffer = rd.storage_buffer_create(forces_bytes.size(), forces_bytes)
	color_buffer = rd.storage_buffer_create(color_bytes.size(), color_bytes)

	var uniforms_a: Array[RDUniform] = []
	_add_uniform(uniforms_a, 0, pos_buffer_a)
	_add_uniform(uniforms_a, 1, vel_buffer_a)
	_add_uniform(uniforms_a, 2, type_buffer)
	_add_uniform(uniforms_a, 3, force_buffer)
	_add_uniform(uniforms_a, 4, color_buffer)
	_add_uniform(uniforms_a, 5, pos_buffer_b)
	_add_uniform(uniforms_a, 6, vel_buffer_b)
	_add_image_uniform(uniforms_a, 7, texture_buffer)
	uniform_set_a = rd.uniform_set_create(uniforms_a, shader, 0)

	var uniforms_b: Array[RDUniform] = []
	_add_uniform(uniforms_b, 0, pos_buffer_b)
	_add_uniform(uniforms_b, 1, vel_buffer_b)
	_add_uniform(uniforms_b, 2, type_buffer)
	_add_uniform(uniforms_b, 3, force_buffer)
	_add_uniform(uniforms_b, 4, color_buffer)
	_add_uniform(uniforms_b, 5, pos_buffer_a)
	_add_uniform(uniforms_b, 6, vel_buffer_a)
	_add_image_uniform(uniforms_b, 7, texture_buffer)
	uniform_set_b = rd.uniform_set_create(uniforms_b, shader, 0)


func _add_uniform(uniforms: Array[RDUniform], binding: int, rid: RID) -> void:
	var uniform := RDUniform.new()
	uniform.binding = binding
	uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
	uniform.add_id(rid)
	uniforms.append(uniform)


func _add_image_uniform(uniforms: Array[RDUniform], binding: int, rid: RID) -> void:
	var uniform := RDUniform.new()
	uniform.binding = binding
	uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	uniform.add_id(rid)
	uniforms.append(uniform)


func run_compute_shader(set_to_use: RID, step: int, delta: float) -> void:
	var compute_list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(compute_list, pipeline)
	rd.compute_list_bind_uniform_set(compute_list, set_to_use, 0)

	var param_bytes: PackedByteArray = []
	param_bytes.append_array(
		(
			PackedVector2Array(
				[
					(
						simulation.REGION_SIZE
						+ Vector2i(particles.particle_radius * 2.0, particles.particle_radius * 2.0)
					),
					simulation.camera_origin
				]
			)
			. to_byte_array()
		)
	)
	param_bytes.append_array(
		(
			PackedInt32Array([particles.particle_count, particles.MAX_PARTICLE_TYPES, step])
			. to_byte_array()
		)
	)
	param_bytes.append_array(
		(
			PackedFloat32Array(
				[
					delta,
					simulation.current_camera_zoom,
					particles.particle_radius,
					particles.dampening,
					particles.repulsion_radius,
					particles.interaction_radius,
					particles.density_limit,
					0.0,
					0.0
				]
			)
			. to_byte_array()
		)
	)

	rd.compute_list_set_push_constant(compute_list, param_bytes, param_bytes.size())

	var groups = particles.particle_count / GROUP_DENSITY + 1
	rd.compute_list_dispatch(compute_list, groups, 1, 1)
	rd.compute_list_end()


func clear_buffers() -> void:
	var rids: Array[RID] = [
		uniform_set_a,
		uniform_set_b,
		pos_buffer_a,
		pos_buffer_b,
		vel_buffer_a,
		vel_buffer_b,
		type_buffer,
		force_buffer,
		color_buffer
	]

	for rid in rids:
		if rid.is_valid():
			rd.free_rid(rid)
