class_name Particles
extends Node

const MAX_PARTICLE_TYPES := 10

var generation_seed := 0
var particle_count: int
var particle_types: int
var reset_colors: bool
var reset_forces: bool
var particle_radius: float
var dampening: float
var repulsion_radius: float
var interaction_radius: float
var density_limit: float

var positions := PackedVector2Array()
var velocities := PackedVector2Array()
var types := PackedInt32Array()
var forces := PackedFloat32Array()
var colors := PackedColorArray()

@onready var simulation := $".."
@onready var pipeline := %Pipeline
@onready var interface := %Interface
@onready var seed_spin_box := $"../Interface/Elements/Sub Elements/Seed Spin Box"


func _ready() -> void:
	seed_spin_box.value = randi_range(0, int(65536 / 2))
	seed(seed_spin_box.value)

	_gen_default_colors()
	_gen_random_force_matrix()


func gen_data() -> void:
	_gen_particles()

	seed(seed_spin_box.value)

	if reset_forces:
		_gen_random_force_matrix()

	if reset_colors:
		_gen_default_colors()

	interface.update_particle_grid()


func _gen_particles() -> void:
	#_add_particle(Vector2(simulation.REGION_SIZE.x / 2.0 - 100.0, simulation.REGION_SIZE.y / 2.0))
	#_add_particle(Vector2(simulation.REGION_SIZE.x / 2.0 + 100.0, simulation.REGION_SIZE.y / 2.0))

	positions.clear()
	velocities.clear()
	types.clear()

	if particle_count == 2:
		_add_particle(
			Vector2(simulation.REGION_SIZE.x / 2.0 - 30.0, simulation.REGION_SIZE.y / 2.0),
			Vector2(),
			randi_range(0, particle_types - 1)
		)
		_add_particle(
			Vector2(simulation.REGION_SIZE.x / 2.0 + 30.0, simulation.REGION_SIZE.y / 2.0),
			Vector2(),
			randi_range(0, particle_types - 1)
		)
		return

	for i in range(particle_count):
		var particle_type := randi_range(0, particle_types - 1)
		var particle_pos := Vector2(
			randf_range(0.0, float(simulation.REGION_SIZE.x)),
			randf_range(0.0, float(simulation.REGION_SIZE.y))
		)
		_add_particle(particle_pos, Vector2(), particle_type)


func _add_particle(pos: Vector2, vel := Vector2(), type := 0) -> void:
	positions.append(pos)
	velocities.append(vel)
	types.append(type)


func _gen_empty_force_matrix() -> void:
	forces.clear()

	for i in range(MAX_PARTICLE_TYPES ** 2):
		forces.append(0.0)


func _gen_random_force_matrix() -> void:
	forces.clear()

	for i in range(MAX_PARTICLE_TYPES ** 2):
		forces.append(randf_range(-1.0, 1.0))


func _gen_default_colors() -> void:
	colors.clear()
	colors.append(Color(0.0, 1.0, 1.0, 1.0))  # 1
	colors.append(Color(1.0, 0.0, 0.0, 1.0))  # 2
	colors.append(Color(0.0, 1.0, 0.0, 1.0))  # 3
	colors.append(Color(1.0, 0.0, 1.0, 1.0))  # 4
	colors.append(Color(1.0, 1.0, 0.0, 1.0))  # 5
	colors.append(Color(0.0, 0.0, 1.0, 1.0))  # 6
	colors.append(Color(1.0, 0.5, 0.0, 1.0))  # 7
	colors.append(Color(0.5, 0.0, 1.0, 1.0))  # 8
	colors.append(Color(0.0, 1.0, 0.5, 1.0))  # 9
	colors.append(Color(1.0, 1.0, 1.0, 1.0))  # 10


func _specify_force_matrix_attraction(type_a: int, type_b: int, force: float) -> void:
	var i := type_a + type_b * MAX_PARTICLE_TYPES
	forces[i] = force
