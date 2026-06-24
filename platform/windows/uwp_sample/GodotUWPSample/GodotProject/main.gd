# main.gd — UWP embedding sample: multiple rotating cubes + host color bus.
#
# Three cubes spin in a row. When embedded in the UWP host we:
#   * push a "cubes_ready" message on _ready so the host can build one button
#     per cube, and
#   * register a "change_cube_color" handler the host calls when a button is
#     clicked; it recolors that cube and pushes a "cube_color_changed" message
#     back so the host can display the new color.
# Guarded by Engine.has_singleton("UWPHost") so the project still runs in the
# editor. Recoloring a cube (via a host button or a direct click) makes it hop
# once so the affected cube is obvious. Left-drag orbits the camera.

extends Node3D

const CUBE_COUNT := 3
const CUBE_SIZE := 1.4
const CUBE_SPACING := 2.2

# One-shot hop played on the cube that was just recolored, so it is obvious
# which cube a button affected.
const JUMP_HEIGHT := 0.9
const JUMP_UP_TIME := 0.16
const JUMP_DOWN_TIME := 0.22

# Left press + drag orbits the camera; a left click (no drag) recolors a cube.
const ORBIT_SENSITIVITY := 0.006
const DRAG_THRESHOLD := 6.0

var _cubes: Array[CSGBox3D] = []
var _jump_tweens: Array[Tween] = [] # parallel to _cubes; current hop per cube
var _host: Object = null

# Orbit-camera state (applied by _update_camera()).
var _orbit_yaw := 0.0
var _orbit_pitch := 0.30
var _orbit_distance := 6.8
var _orbit_target := Vector3(0.0, 0.4, 0.0)
var _left_down := false
var _dragged := false
var _press_pos := Vector2.ZERO

@onready var _label: Label = $UI/InfoLabel
@onready var _camera: Camera3D = $Camera


func _ready() -> void:
	_spawn_environment()
	_spawn_ground()
	_spawn_cubes()
	_update_camera()

	print("[UWP Embed Test] _ready — rendering driver: ",
			RenderingServer.get_current_rendering_driver_name())
	print("[UWP Embed Test] display server: ", DisplayServer.get_name())
	print("[UWP Embed Test] window size: ", DisplayServer.window_get_size())

	# Talk to the UWP host only when embedded (absent in the editor).
	if Engine.has_singleton("UWPHost"):
		_host = Engine.get_singleton("UWPHost")
		_host.register_handler("change_cube_color", _on_change_cube_color)
		_host.send_to_host("cubes_ready", _describe_cubes())


func _spawn_environment() -> void:
	# Dark gradient sky so the floor blends into a backdrop instead of the flat
	# default clear color. Horizon is kept near the floor color to hide the seam,
	# and a little sky-derived ambient softens the cubes' shadowed faces.
	var sky_mat := ProceduralSkyMaterial.new()
	sky_mat.sky_top_color = Color(0.04, 0.05, 0.09)
	sky_mat.sky_horizon_color = Color(0.10, 0.11, 0.16)
	sky_mat.ground_bottom_color = Color(0.05, 0.05, 0.07)
	sky_mat.ground_horizon_color = Color(0.10, 0.11, 0.16)

	var sky := Sky.new()
	sky.sky_material = sky_mat

	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	env.sky = sky
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.ambient_light_energy = 0.4

	var world_env := WorldEnvironment.new()
	world_env.name = "WorldEnvironment"
	world_env.environment = env
	add_child(world_env)


func _spawn_ground() -> void:
	# A wide, thin dark slab the cubes rest on; the Sun casts their shadows onto it.
	var ground := CSGBox3D.new()
	ground.name = "Ground"
	ground.size = Vector3(14.0, 0.4, 8.0)
	# Top surface sits flush under the cubes (cube bottom = -CUBE_SIZE * 0.5).
	ground.position = Vector3(0.0, -CUBE_SIZE * 0.5 - 0.2, 0.0)
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.07, 0.07, 0.09)
	ground.material = mat
	add_child(ground)


func _spawn_cubes() -> void:
	var colors := [
		Color(0.91, 0.27, 0.38), # red
		Color(0.30, 0.69, 0.40), # green
		Color(0.27, 0.52, 0.91), # blue
	]
	var start_x := -CUBE_SPACING * (CUBE_COUNT - 1) * 0.5
	for i in CUBE_COUNT:
		var cube := CSGBox3D.new()
		cube.name = "Cube%d" % i
		cube.size = Vector3(CUBE_SIZE, CUBE_SIZE, CUBE_SIZE)
		cube.position = Vector3(start_x + CUBE_SPACING * i, 0.0, 0.0)
		var mat := StandardMaterial3D.new()
		mat.albedo_color = colors[i % colors.size()]
		cube.material = mat
		add_child(cube)
		_cubes.append(cube)
		_jump_tweens.append(null)


func _process(delta: float) -> void:
	for i in _cubes.size():
		var cube := _cubes[i]
		# Spin in place about the vertical axis only, so each cube stays flat on
		# the plane. Tilting (rotate_x) would swing the corners below the flat
		# bottom and clip through the ground.
		cube.rotate_y(delta * (1.2 + 0.25 * i))
	_label.text = "Godot %s in UWP SwapChainPanel\nFPS: %d  |  Size: %s  |  Cubes: %d" % [
		Engine.get_version_info()["string"],
		Engine.get_frames_per_second(),
		DisplayServer.window_get_size(),
		_cubes.size(),
	]


# Mouse interaction in the 3D view:
#   * left press + drag    -> orbit the camera around the cubes
#   * left click (no drag)  -> recolor the nearest cube (input-injection smoke
#     test; also works in the editor)
func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_left_down = true
			_dragged = false
			_press_pos = event.position
		else:
			# Treat as a click only if the press never turned into a drag.
			if _left_down and not _dragged:
				var idx := _pick_cube(event.position)
				if idx >= 0:
					_recolor_cube(idx)
			_left_down = false
	elif event is InputEventMouseMotion and _left_down:
		if not _dragged and event.position.distance_to(_press_pos) > DRAG_THRESHOLD:
			_dragged = true
		if _dragged:
			_orbit_yaw -= event.relative.x * ORBIT_SENSITIVITY
			_orbit_pitch = clampf(_orbit_pitch + event.relative.y * ORBIT_SENSITIVITY, 0.1, 1.4)
			_update_camera()


# Host -> engine: invoked via godot_uwp_call_engine("change_cube_color", "[i]").
# Loosely typed so a JSON number (float) is accepted regardless.
func _on_change_cube_color(index) -> void:
	_recolor_cube(int(index))


func _recolor_cube(index: int) -> void:
	if index < 0 or index >= _cubes.size():
		return
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(randf(), randf(), randf())
	_cubes[index].material = mat
	_jump_cube(index)
	var hex := _cube_color_hex(index)
	print("[UWP Embed Test] cube %d -> #%s" % [index, hex])
	# Engine -> host: report the new color so the host can show it.
	if _host != null:
		_host.send_to_host("cube_color_changed", [index, String(_cubes[index].name), hex])


# Describes every cube for the host: one entry -> one button.
func _describe_cubes() -> Array:
	var list := []
	for i in _cubes.size():
		list.append({
			"index": i,
			"name": String(_cubes[i].name),
			"color": _cube_color_hex(i),
		})
	return list


func _cube_color_hex(index: int) -> String:
	var mat := _cubes[index].material as StandardMaterial3D
	return mat.albedo_color.to_html(false) if mat != null else "ffffff"


# Index of the cube whose screen-projected center is nearest p_pos, or -1.
# CSG nodes have no physics body, so pick by projected distance instead of a ray.
func _pick_cube(p_pos: Vector2) -> int:
	var camera := get_viewport().get_camera_3d()
	if camera == null:
		return -1
	var best := -1
	var best_dist := INF
	for i in _cubes.size():
		var center: Vector3 = _cubes[i].global_position
		if camera.is_position_behind(center):
			continue
		var screen := camera.unproject_position(center)
		var d := screen.distance_to(p_pos)
		if d < best_dist:
			best_dist = d
			best = i
	# Only accept clicks reasonably close to a cube.
	return best if best_dist < 200.0 else -1


# One hop up and back down on the given cube. Runs alongside the spin and
# restarts cleanly if the cube is still mid-hop.
func _jump_cube(index: int) -> void:
	if index < 0 or index >= _cubes.size():
		return
	var cube := _cubes[index]
	var existing := _jump_tweens[index]
	if existing != null and existing.is_valid():
		existing.kill()
	cube.position = Vector3(cube.position.x, 0.0, cube.position.z)
	var t := cube.create_tween()
	t.tween_property(cube, "position:y", JUMP_HEIGHT, JUMP_UP_TIME).set_trans(Tween.TRANS_QUAD).set_ease(Tween.EASE_OUT)
	t.tween_property(cube, "position:y", 0.0, JUMP_DOWN_TIME).set_trans(Tween.TRANS_QUAD).set_ease(Tween.EASE_IN)
	_jump_tweens[index] = t


# Places the camera from the orbit yaw/pitch/distance around _orbit_target.
func _update_camera() -> void:
	var offset := Vector3(
		cos(_orbit_pitch) * sin(_orbit_yaw),
		sin(_orbit_pitch),
		cos(_orbit_pitch) * cos(_orbit_yaw),
	) * _orbit_distance
	_camera.position = _orbit_target + offset
	_camera.look_at(_orbit_target, Vector3.UP)
