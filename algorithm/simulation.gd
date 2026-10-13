class_name Simulation
extends Node2D

const REGION_SIZE := Vector2i(2560, 1440)

var is_active := true
var camera_origin: Vector2 = REGION_SIZE / 2.0
var camera_zoom := 1.0
var current_camera_zoom := 1.0
var mouse_position: Vector2
var mouse_change: Vector2
var current_mouse_change: Vector2

@onready var particles := %Particles
@onready var pipeline := %Pipeline
@onready var interface := %Interface
@onready var elements := %Elements
@onready var post_processing := %"Post Processing"


func _ready() -> void:
	interface.update_simulation_values()
	reset()


func _process(delta: float) -> void:
	if Input.is_key_pressed(KEY_ESCAPE):
		get_tree().quit()
	elif Input.is_action_just_pressed("pause_simulation"):
		is_active = not is_active
	elif Input.is_action_just_pressed("open_settings"):
		elements.visible = not elements.visible
		elements.process_mode = (
			Node.PROCESS_MODE_INHERIT
			if elements.process_mode == Node.PROCESS_MODE_DISABLED
			else Node.PROCESS_MODE_DISABLED
		)
		post_processing.environment.glow_enabled = not elements.visible and interface.glow_enabled
	elif Input.is_action_just_pressed("reset_simulation"):
		interface.update_simulation_values()
		reset()
	elif Input.is_action_just_pressed("toggle_fullscreen"):
		DisplayServer.window_set_mode(
			(
				DisplayServer.WINDOW_MODE_FULLSCREEN
				if DisplayServer.window_get_mode() == DisplayServer.WINDOW_MODE_WINDOWED
				else DisplayServer.WINDOW_MODE_WINDOWED
			)
		)

	var new_mouse_position := get_local_mouse_position()
	mouse_change = new_mouse_position - mouse_position
	mouse_position = new_mouse_position

	if not interface.mouse_within or not elements.visible:
		if Input.is_mouse_button_pressed(MOUSE_BUTTON_LEFT):
			current_mouse_change += mouse_change * delta
			camera_origin += current_mouse_change / camera_zoom

		if Input.is_action_just_pressed("scroll_up"):
			camera_zoom += 0.25 * camera_zoom
		elif Input.is_action_just_pressed("scroll_down"):
			camera_zoom -= 0.25 * camera_zoom

		camera_zoom = clamp(camera_zoom, 1.0, 10.0)

	if not Input.is_mouse_button_pressed(MOUSE_BUTTON_LEFT):
		mouse_change = Vector2()
		camera_origin += current_mouse_change / camera_zoom

	current_camera_zoom = lerpf(current_camera_zoom, camera_zoom, delta * 4.0)
	current_mouse_change = lerp(current_mouse_change, mouse_change, delta * 4.0)


func reset() -> void:
	particles.gen_data()
	pipeline.clear_buffers()
	pipeline.create_buffers()
