# Renders the weapon icons (ui/icons/weapons/<id>.png, 512x256, transparent) from the same held models as WeaponFx.cs.
# Run by `make shooter-icons` (see Makefile). Side-on orthographic camera, flat ambient light, auto-fit to the model.
# The MODELS table mirrors WeaponFx.Models; keep the two in step when a weapon is added.
extends SceneTree

const OUT_W := 512
const OUT_H := 256
const PACK := "quaternius-ultimate-gun-pack/"
# id -> [file in assets/quaternius-ultimate-gun-pack/, longest edge in metres]
const MODELS := {
	"pistol": ["pistol-1.glb", 0.26],
	"ak47": ["assault-rifle-1.glb", 0.8],
	"sniper": ["sniper-rifle.glb", 1.15],
	"knife": ["bayonet.glb", 0.32],
	"m4a1": ["assault-rifle-2.glb", 0.86],
	"rpk": ["assault-rifle-3.glb", 1.0],
	"pkm": ["assault-rifle-4.glb", 1.1],
	"m249": ["assault-rifle-5.glb", 1.04],
	"g36": ["assault-rifle2-1.glb", 0.99],
	"m60": ["assault-rifle2-3.glb", 1.1],
	"aug": ["bullpup-1.glb", 0.73],
	"p90": ["bullpup-2.glb", 0.5],
	"mp5": ["submachine-gun-1.glb", 0.6],
	"ump45": ["submachine-gun-2.glb", 0.66],
	"remington870": ["shotgun-1.glb", 1.0],
	"spas12": ["shotgun-2.glb", 0.9],
	"benelli_m4": ["shotgun-3.glb", 0.95],
	"aa12": ["shotgun-4.glb", 0.9],
	"glock17": ["pistol-2.glb", 0.22],
	"deagle": ["pistol-3.glb", 0.3],
	"m82": ["sniper-rifle-2.glb", 1.3],
	"awm": ["sniper-rifle-3.glb", 1.2],
	"svd": ["sniper-rifle-4.glb", 1.2],
	"bazooka": ["", 0.0],
}

var _vp: SubViewport
var _world: Node3D
var _cam: Camera3D


func _initialize() -> void:
	var repo_assets := ProjectSettings.globalize_path("res://") + "../../assets/"
	var out_dir := ProjectSettings.globalize_path("res://ui/icons/weapons/")
	_vp = SubViewport.new()
	_vp.size = Vector2i(OUT_W, OUT_H)
	_vp.transparent_bg = true
	_vp.own_world_3d = true
	_vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_vp)
	_world = Node3D.new()
	_vp.add_child(_world)
	_cam = Camera3D.new()
	_cam.projection = Camera3D.PROJECTION_ORTHOGONAL
	_cam.near = 0.01
	_cam.far = 50.0
	_vp.add_child(_cam)
	_cam.current = true

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0, 0, 0, 0)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.85, 0.85, 0.85)
	env.ambient_light_energy = 1.0
	var we := WorldEnvironment.new()
	we.environment = env
	_world.add_child(we)
	var sun := DirectionalLight3D.new()
	sun.light_energy = 0.6
	sun.shadow_enabled = false
	_world.add_child(sun)
	sun.look_at_from_position(Vector3(1.5, 2.0, 2.0), Vector3.ZERO, Vector3.UP)

	await process_frame
	var failures := 0
	for id in MODELS:
		var spec: Array = MODELS[id]
		var holder := Node3D.new()
		_world.add_child(holder)
		var model: Node3D = null
		if id == "bazooka":
			model = _bazooka()
			holder.add_child(model)
		else:
			model = _load_glb(repo_assets + PACK + spec[0])
			if model == null:
				printerr("skip ", id, ": ", spec[0])
				failures += 1
				holder.queue_free()
				continue
			# same fit as WeaponFx.Make: barrel along +X in the model, grip at the origin, muzzle towards -Z
			var box := _local_box(model)
			var s: float = float(spec[1]) / maxf(box.size.x, 0.001)
			holder.scale = Vector3.ONE * s
			holder.rotation_degrees = Vector3(0, 90, 0)
			holder.add_child(model)
			model.position = Vector3(-(box.position.x + box.size.x * 0.3), -(box.position.y + box.size.y * 0.8), -(box.position.z + box.size.z * 0.5))
		await process_frame
		var world_box := _world_box(holder)
		var c := world_box.get_center()
		_cam.look_at_from_position(c + Vector3(5, 0, 0), c, Vector3.UP)
		# ortho size is the full view height; the view is 2:1, so the width is twice that
		_cam.size = maxf(world_box.size.y * 1.15, world_box.size.z * 1.15 / 2.0)
		await process_frame
		await process_frame
		var img := _vp.get_texture().get_image()
		img.convert(Image.FORMAT_RGBA8)
		var path: String = out_dir + id + ".png"
		if img.save_png(path) != OK:
			printerr("save failed: ", path)
			failures += 1
		else:
			print("rendered ", id)
		holder.queue_free()
		await process_frame
	print("done, failures: ", failures)
	quit(1 if failures > 0 else 0)


func _load_glb(path: String) -> Node3D:
	if not FileAccess.file_exists(path):
		return null
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	if doc.append_from_file(path, state) != OK:
		return null
	return doc.generate_scene(state) as Node3D


# The tube stand-in used by WeaponFx.BuildTube: a dark cylinder laid along -Z, 0.82 m long.
func _bazooka() -> Node3D:
	var n := Node3D.new()
	var mi := MeshInstance3D.new()
	var cyl := CylinderMesh.new()
	cyl.top_radius = 0.06
	cyl.bottom_radius = 0.06
	cyl.height = 0.82
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.22, 0.25, 0.2)
	mat.roughness = 0.8
	cyl.material = mat
	mi.mesh = cyl
	mi.rotation_degrees = Vector3(90, 0, 0)
	mi.position = Vector3(0, 0, -0.41)
	n.add_child(mi)
	return n


func _meshes(node: Node, out: Array) -> Array:
	if node is MeshInstance3D:
		out.append(node)
	for c in node.get_children():
		_meshes(c, out)
	return out


# Bounds of the model's meshes in the model's own space. The model is parented under the world only
# long enough to get global transforms, then the caller re-parents it under the holder.
func _local_box(model: Node3D) -> AABB:
	_world.add_child(model)
	var box := AABB()
	var first := true
	for mi in _meshes(model, []):
		var b: AABB = (model.global_transform.affine_inverse() * mi.global_transform) * mi.get_aabb()
		box = b if first else box.merge(b)
		first = false
	_world.remove_child(model)
	return box


# Bounds of everything under the holder, in world space (the holder is in the tree).
func _world_box(holder: Node3D) -> AABB:
	var box := AABB()
	var first := true
	for mi in _meshes(holder, []):
		var b: AABB = mi.global_transform * mi.get_aabb()
		box = b if first else box.merge(b)
		first = false
	return box
