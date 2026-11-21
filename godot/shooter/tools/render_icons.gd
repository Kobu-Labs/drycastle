# Renders the weapon icons (ui/icons/weapons/<id>.png, 512x256, transparent) from the same held models as WeaponFx.cs,
# and the item icons (ui/icons/items/<id>.png, 128x128, transparent) from the worn models of items.json.
# Run by `make shooter-icons` (see Makefile). Orthographic camera, flat ambient light, auto-fit to the model. Weapons are shot side-on;
# items from the front, a little from the side and above (a worn model faces -Z, so the camera is on the -Z side).
# `godot --path godot/shooter --script res://tools/render_icons.gd -- items` renders only the items (the weapons are left as they are).
# The MODELS table mirrors WeaponFx.Models, the ITEMS table the `model` of items.json; keep each in step when one is added.
extends SceneTree

const OUT_W := 512
const OUT_H := 256
const ITEM_SIZE := 128
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
	"hunting_rifle": ["", 1.25], # Synty Apocalypse, see SYNTY and WeaponFx.SyntyModels
	"rpg": ["", 0.0],
	"strela": ["", 0.0],
}

# Weapons of the paid Synty packs (WeaponFx.SyntyModels): id -> [glb, atlas], both under assets-private/ (DRYCASTLE_ASSETS_PRIVATE overrides it). Barrel along +Z.
# `-- hunting_rifle` (any weapon ids) renders only those.
const SYNTY := {
	"hunting_rifle": ["synty/glb/apocalypse/SM_Wep_HuntingRifle_Clean_01.glb", "synty/glb/apocalypse/tex/PolygonApocalypse_01_A.png"],
}

# id -> [model in assets/ (items.json `model`), yaw degrees to the right of the item's front, pitch degrees above it]
const ITEMS := {
	"bigfoot_head": ["drycastle-generated-models/bigfoot_head.glb", 20.0, 10.0],
	"gold_chain": ["drycastle-generated-models/gold_chain.glb", 0.0, 18.0],
	# a model of a paid Synty pack (items.json model + atlas, under assets-private/, DRYCASTLE_ASSETS_PRIVATE overrides it): [model, yaw, pitch, atlas]; the pack's props face +Z
	"hunting_binoculars": ["synty/glb/shops/SM_Prop_Hunting_Binoculars_01.glb", 160.0, 22.0, "synty/glb/shops/tex/PolygonShops_Texture_01_A.png"],
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
	var only_items := OS.get_cmdline_user_args().has("items")
	for a in OS.get_cmdline_user_args():
		if ITEMS.has(a):
			only_items = true
	var only_ids := []
	for a in OS.get_cmdline_user_args():
		if MODELS.has(a):
			only_ids.append(a)
	for id in ([] if only_items else (only_ids if not only_ids.is_empty() else MODELS.keys())):
		var spec: Array = MODELS[id]
		var holder := Node3D.new()
		_world.add_child(holder)
		var model: Node3D = null
		if id == "rpg":
			model = _rpg()
			holder.add_child(model)
		elif id == "strela":
			model = _strela()
			holder.add_child(model)
		elif SYNTY.has(id):
			var priv := OS.get_environment("DRYCASTLE_ASSETS_PRIVATE")
			if priv == "":
				priv = repo_assets + "../assets-private"
			model = _load_glb(priv + "/" + SYNTY[id][0])
			if model == null:
				printerr("skip ", id, ": ", SYNTY[id][0])
				failures += 1
				holder.queue_free()
				continue
			var mat := StandardMaterial3D.new()
			mat.albedo_texture = ImageTexture.create_from_image(Image.load_from_file(priv + "/" + SYNTY[id][1]))
			mat.texture_filter = BaseMaterial3D.TEXTURE_FILTER_NEAREST
			mat.roughness = 0.8
			for mi in _meshes(model, []):
				mi.material_override = mat
			# same fit as WeaponFx.Make for a Synty weapon: barrel along +Z, grip (rear third) at the origin, muzzle towards -Z
			var zbox := _local_box(model)
			var zs: float = float(spec[1]) / maxf(zbox.size.z, 0.001)
			holder.scale = Vector3.ONE * zs
			holder.rotation_degrees = Vector3(0, 180, 0)
			holder.add_child(model)
			model.position = Vector3(-(zbox.position.x + zbox.size.x * 0.5), -(zbox.position.y + zbox.size.y * 0.8), -(zbox.position.z + zbox.size.z * 0.3))
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
	failures += await _render_items(repo_assets)
	print("done, failures: ", failures)
	quit(1 if failures > 0 else 0)


# One square render per item: the model auto-fitted, seen from the front, a little from the side and above.
func _render_items(repo_assets: String) -> int:
	var out_dir := ProjectSettings.globalize_path("res://ui/icons/items/")
	DirAccess.make_dir_recursive_absolute(out_dir)
	_vp.size = Vector2i(ITEM_SIZE, ITEM_SIZE)
	var failures := 0
	var only := []
	for a in OS.get_cmdline_user_args():
		if ITEMS.has(a):
			only.append(a)
	for id in ITEMS:
		if not only.is_empty() and not only.has(id):
			continue
		var spec: Array = ITEMS[id]
		var synty := String(spec[0]).begins_with("synty/")
		var priv := OS.get_environment("DRYCASTLE_ASSETS_PRIVATE")
		if priv == "":
			priv = repo_assets + "../assets-private"
		var model := _load_glb((priv + "/" if synty else repo_assets) + String(spec[0]))
		if model != null and synty:
			var mat := StandardMaterial3D.new()
			mat.albedo_texture = ImageTexture.create_from_image(Image.load_from_file(priv + "/" + String(spec[3])))
			mat.texture_filter = BaseMaterial3D.TEXTURE_FILTER_NEAREST
			mat.roughness = 0.8
			for mi in _meshes(model, []):
				mi.material_override = mat
		if model == null:
			printerr("skip item ", id, ": ", spec[0])
			failures += 1
			continue
		var holder := Node3D.new()
		_world.add_child(holder)
		holder.add_child(model)
		await process_frame
		var box := _world_box(holder)
		var c := box.get_center()
		var yaw := deg_to_rad(float(spec[1]))
		var pitch := deg_to_rad(float(spec[2]))
		# the front of a worn model is -Z: the camera stands on that side
		var dir := Vector3(sin(yaw) * cos(pitch), sin(pitch), -cos(yaw) * cos(pitch))
		_cam.look_at_from_position(c + dir * 5.0, c, Vector3.UP)
		# a square view that holds the whole model from any side: the box's diagonal, with a little air
		_cam.size = box.size.length() * 0.8
		await process_frame
		await process_frame
		var img := _vp.get_texture().get_image()
		img.convert(Image.FORMAT_RGBA8)
		var path: String = out_dir + id + ".png"
		if img.save_png(path) != OK:
			printerr("save failed: ", path)
			failures += 1
		else:
			print("rendered item ", id)
		holder.queue_free()
		await process_frame
	return failures


func _load_glb(path: String) -> Node3D:
	if not FileAccess.file_exists(path):
		return null
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	if doc.append_from_file(path, state) != OK:
		return null
	return doc.generate_scene(state) as Node3D


# The tube stand-in used by WeaponFx.BuildTube: a dark cylinder laid along -Z, 0.82 m long.
func _rpg() -> Node3D:
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


# The Lock-on launcher (issue #347): the code-built stand-in of WeaponFx.BuildLockOnLauncher, seen from the side.
func _strela() -> Node3D:
	var n := Node3D.new()
	var olive := StandardMaterial3D.new()
	olive.albedo_color = Color(0.27, 0.31, 0.2)
	olive.roughness = 0.75
	var dark := StandardMaterial3D.new()
	dark.albedo_color = Color(0.12, 0.13, 0.12)
	dark.roughness = 0.7
	var tube := CylinderMesh.new()
	tube.top_radius = 0.045
	tube.bottom_radius = 0.045
	tube.height = 1.3
	tube.material = olive
	var body := MeshInstance3D.new()
	body.mesh = tube
	body.rotation_degrees = Vector3(90, 0, 0)
	body.position = Vector3(0, 0, -0.22)
	n.add_child(body)
	for spec in [[Vector3(0.05, 0.07, 0.16), Vector3(0.03, 0.075, -0.25)], [Vector3(0.04, 0.13, 0.05), Vector3(0, -0.11, -0.05)], [Vector3(0.04, 0.1, 0.05), Vector3(0, -0.095, -0.5)], [Vector3(0.14, 0.14, 0.1), Vector3(0, 0, 0.48)]]:
		var box := BoxMesh.new()
		box.size = spec[0]
		box.material = dark
		var part := MeshInstance3D.new()
		part.mesh = box
		part.position = spec[1]
		n.add_child(part)
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
