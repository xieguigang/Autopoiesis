class_name Interface
extends Control

var particle_count: int
var particle_types: int
var particle_force_buttons: Array[Button]
var particle_color_pickers_a: Array[ColorPickerButton]
var particle_color_pickers_b: Array[ColorPickerButton]
var glow_enabled := false
var mouse_within := false

@onready var simulation := $".."
@onready var particles := %Particles
@onready var panel := %Panel
@onready var sub_elements := %"Sub Elements"
@onready var particle_count_label := $"Elements/Sub Elements/Particle Count Label"
@onready var particle_count_slider := $"Elements/Sub Elements/Particle Count Slider"
@onready var particle_types_label := $"Elements/Sub Elements/Particle Types Label"
@onready var particle_types_slider := $"Elements/Sub Elements/Particle Types Slider"
@onready var regenerate_colors_check_button := $"Elements/Sub Elements/Regenerate Colors"
@onready var regenerate_forces_check_button := $"Elements/Sub Elements/Regenerate Forces"
@onready var seed_spin_box := $"Elements/Sub Elements/Seed Spin Box"
@onready var particle_radius_label := $"Elements/Sub Elements/Particle Radius Label"
@onready var particle_radius_slider := $"Elements/Sub Elements/Particle Radius Slider"
@onready var dampening_label := $"Elements/Sub Elements/Dampening Label"
@onready var dampening_slider := $"Elements/Sub Elements/Dampening Slider"
@onready var repulsion_label := $"Elements/Sub Elements/Repulsion Label"
@onready var repulsion_slider := $"Elements/Sub Elements/Repulsion Slider"
@onready var interaction_label := $"Elements/Sub Elements/Interaction Label"
@onready var interaction_slider := $"Elements/Sub Elements/Interaction Slider"
@onready var density_limit_label := $"Elements/Sub Elements/Density Limit Label"
@onready var density_limit_slider := $"Elements/Sub Elements/Density Limit Slider"
@onready var particle_grid := $"Elements/Sub Elements/Particle Grid"
@onready var glow_check_button := $"Elements/Sub Elements/Glow Check Button"


func _process(_delta: float) -> void:
	particle_count = int(max(2.0, pow(particle_count_slider.value, 2.0)))
	particle_count_label.text = "Particle Count:  " + str(particle_count)

	particle_types = int(particle_types_slider.value)
	particle_types_label.text = "Particle Types:  " + str(particle_types)

	particles.particle_radius = particle_radius_slider.value
	particle_radius_label.text = "Particle Radius:  " + str(int(particles.particle_radius))

	particles.dampening = dampening_slider.value
	dampening_label.text = "Dampening:  %.2f" % particles.dampening

	particles.repulsion_radius = repulsion_slider.value
	repulsion_label.text = "Repulsion Radius:  " + str(int(particles.repulsion_radius))

	particles.interaction_radius = interaction_slider.value
	interaction_label.text = "Interaction Radius:  " + str(int(particles.interaction_radius))

	particles.density_limit = density_limit_slider.value
	density_limit_label.text = "Density Limit:  " + str(int(particles.density_limit))

	for i in range(particles.particle_types):
		var color_picker_button_a := particle_color_pickers_a[i]
		var color_picker_button_b := particle_color_pickers_b[i]
		var current_color: Color = particles.colors[i]

		if color_picker_button_a.color != current_color:
			particles.colors[i] = color_picker_button_a.color
			color_picker_button_b.color = color_picker_button_a.color
		elif color_picker_button_b.color != current_color:
			particles.colors[i] = color_picker_button_b.color
			color_picker_button_a.color = color_picker_button_b.color

	var force_i := 0

	for i in range(particles.particle_types ** 2):
		var particle_force_button := particle_force_buttons[i]

		if i % particles.particle_types == 0 and i != 0:
			force_i += particles.MAX_PARTICLE_TYPES - particles.particle_types

		if particle_force_button.is_hovered():
			var force_change := (
				1
				if Input.is_action_just_pressed("scroll_up")
				else -1 if Input.is_action_just_pressed("scroll_down") else 0
			)

			if Input.is_mouse_button_pressed(MOUSE_BUTTON_RIGHT):
				particles.forces[force_i] = 0.0
			elif force_change != 0:
				particles.forces[force_i] += force_change * 0.1
				particles.forces[force_i] = clampf(particles.forces[force_i], -1.0, 1.0)
			else:
				continue

			var color := _calc_particle_force_button_color(particles.forces[force_i])
			_update_particle_force_button_color(particle_force_button, color)

		force_i += 1

	glow_enabled = glow_check_button.button_pressed

	var rect: Rect2 = panel.get_global_rect()
	mouse_within = rect.has_point(simulation.mouse_position)
	mouse_within = mouse_within or simulation.mouse_position.x < 0.0


func update_particle_grid() -> void:
	particle_grid.columns = particles.particle_types + 1

	for child in particle_grid.get_children():
		child.free()

	particle_force_buttons.clear()
	particle_color_pickers_a.clear()
	particle_color_pickers_b.clear()

	var button_width: float = (sub_elements.size.x - 0.0) / (particles.particle_types + 1)
	var force_i := 0

	for y in range(particles.particle_types + 1):
		if y > 1:
			force_i += particles.MAX_PARTICLE_TYPES - particles.particle_types

		for x in range(particles.particle_types + 1):
			var color: Color
			var button_array: Array = []
			var use_normal_button := true

			if y == 0 and x > 0:
				color = particles.colors[x - 1]
				button_array = particle_color_pickers_a
				use_normal_button = false
			elif y > 0 and x == 0:
				color = particles.colors[y - 1]
				button_array = particle_color_pickers_b
				use_normal_button = false
			else:
				var force: float = particles.forces[force_i]
				color = _calc_particle_force_button_color(force)
				button_array = particle_force_buttons

			var button = Button.new() if use_normal_button else ColorPickerButton.new()
			button.custom_minimum_size = Vector2(button_width, button_width)
			button.focus_mode = Control.FOCUS_NONE

			if use_normal_button:
				var normal_stylebox := button.get_theme_stylebox("normal").duplicate()
				normal_stylebox.bg_color = color
				button.add_theme_stylebox_override("normal", normal_stylebox)

				var hover_stylebox := normal_stylebox.duplicate()
				hover_stylebox.bg_color = color.lightened(0.2)
				button.add_theme_stylebox_override("hover", hover_stylebox)

				var pressed_stylebox := normal_stylebox.duplicate()
				pressed_stylebox.bg_color = color.darkened(0.2)
				button.add_theme_stylebox_override("pressed", pressed_stylebox)
			else:
				button.color = color

			if x == 0 and y == 0:
				button.disabled = true
				button.focus_mode = 0
			elif x > 0 and y > 0:
				force_i += 1
				button_array.append(button)
			else:
				button_array.append(button)

			particle_grid.add_child(button)


func _calc_particle_force_button_color(force: float) -> Color:
	return (
		Color.from_hsv(0, abs(force), abs(force))
		if force < 0.0
		else Color.from_hsv(0.333, force, force)
	)


func _update_particle_force_button_color(button: Button, color: Color) -> void:
	var normal_stylebox := button.get_theme_stylebox("normal")
	normal_stylebox.bg_color = color

	var hover_stylebox := button.get_theme_stylebox("hover")
	hover_stylebox.bg_color = color.lightened(0.2)

	var pressed_stylebox := button.get_theme_stylebox("pressed")
	pressed_stylebox.bg_color = color.darkened(0.2)


func update_simulation_values() -> void:
	_process(0.0)

	particles.particle_count = particle_count
	particles.particle_types = particle_types
	particles.reset_colors = regenerate_colors_check_button.button_pressed
	particles.reset_forces = regenerate_forces_check_button.button_pressed
