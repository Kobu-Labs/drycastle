# Step 4 of the SIMPLE Military vehicle batch (scripts/military_kit/build_simple_military.sh): reloads the built GLBs the way the game does (GLTFDocument),
# checks the node names the standard (docs/standards/car-design.md) and the game look for, measures them and checks the pack material names
# against glb/simple-military/materials.json.
#   godot --headless --path assets-private/synty/simple-military --script <repo>/scripts/military_kit/simple_military_check.gd -- <glb dir>
# Exit code 1 when a required node or material is missing.
extends SceneTree

const REQUIRED := {
	"humvee": ["hull", "wheel_fl_steer", "wheel_fr_steer", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr", "glass_0", "glass_5", "steering_wheel_rim", "steering_wheel_spokes",
		"seat_driver", "seat_passenger_1", "seat_passenger_2", "seat_passenger_3", "seat_gunner", "gun_mount", "gun", "barrel", "muzzle", "gunner_sight",
		"light_head_l", "light_head_r", "light_tail_l", "light_tail_r", "tow_hitch", "tow_hook"],
	"scud": ["hull", "wheel_l1_steer", "wheel_r1_steer", "wheel_l2_steer", "wheel_r2_steer", "wheel_l1", "wheel_r1", "wheel_l2", "wheel_r2", "wheel_l3", "wheel_r3", "wheel_l4", "wheel_r4",
		"glass_0", "glass_7", "steering_wheel_rim", "steering_wheel_spokes", "seat_driver", "seat_operator", "rack", "missile", "light_head_l", "light_head_r", "light_tail_l", "light_tail_r"],
	"scud_missile": ["missile"],
	"rocket_cart": ["hull", "wheel_l", "wheel_r", "missile", "tow_eye", "axle", "light_tail_l", "light_tail_r"],
}
const PACK_MATERIALS := ["Military_Green", "Military_Green_2s"]


func _init() -> void:
	var dir := String(OS.get_cmdline_user_args()[0]).trim_suffix("/")
	var mats: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(dir + "/materials.json"))["materials"]
	var bad := 0
	for name in REQUIRED:
		var doc := GLTFDocument.new()
		var state := GLTFState.new()
		if doc.append_from_file("%s/%s.glb" % [dir, name], state) != OK:
			print("FAIL load ", name)
			bad += 1
			continue
		var scene: Node = doc.generate_scene(state)
		for n in REQUIRED[name]:
			if scene.name != n and scene.find_child(n, true, false) == null:
				print("MISSING node ", name, "/", n)
				bad += 1
		var top: Array = []
		for c in scene.get_children():
			top.append(c.name)
		print("%s: root %s, children %s; hull children with glass_/seat_/light_: %s" % [name, scene.name, str(top), str(_named(scene.find_child("hull", true, false), ["glass_", "seat_", "light_"]))])
		var meshes: Array = []
		_collect(scene, Transform3D.IDENTITY, meshes)
		var box := AABB()
		var first := true
		var tris := 0
		var names := {}
		for mi in meshes:
			var m: Mesh = mi["mesh"]
			var b: AABB = mi["xf"] * m.get_aabb()
			box = b if first else box.merge(b)
			first = false
			for s in m.get_surface_count():
				var a := m.surface_get_arrays(s)
				var idx = a[Mesh.ARRAY_INDEX]
				tris += (idx.size() if idx != null else (a[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()) / 3
				var mat := m.surface_get_material(s)
				var mn := mat.resource_name if mat != null else ""
				names[mn] = true
				if PACK_MATERIALS.has(mn) and not mats.has(mn):
					print("NO MATERIAL ENTRY ", name, " ", mn)
					bad += 1
		print("%s: %d tris, size x %.2f y %.2f z %.2f, min (%.2f %.2f %.2f), materials %s" % [name, tris, box.size.x, box.size.y, box.size.z, box.position.x, box.position.y, box.position.z, str(names.keys())])
		if name == "humvee":
			_report_wheels(scene, ["wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr"])
			print("  hull rest origin ", (scene.find_child("hull", true, false) as Node3D).position, ", wheel_fl world ", _world(scene, "wheel_fl"), ", glass_0 mesh ", scene.find_child("glass_0", true, false).get_class())
		if name == "scud":
			_report_wheels(scene, ["wheel_l1", "wheel_r1", "wheel_l2", "wheel_l3", "wheel_l4", "wheel_r4"])
			_check_rack(scene)
		if name == "rocket_cart":
			_report_wheels(scene, ["wheel_l", "wheel_r"])
			print("  tow_eye ", _world(scene, "tow_eye"), " axle ", _world(scene, "axle"), " missile ", _world(scene, "missile"))
		scene.free()
	print("check: ", "OK" if bad == 0 else "%d problem(s)" % bad)
	quit(1 if bad > 0 else 0)


func _world(scene: Node, n: String) -> Vector3:
	var node := scene.find_child(n, true, false) as Node3D
	var t := Transform3D.IDENTITY
	while node != null and node != scene:
		t = node.transform * t
		node = node.get_parent() as Node3D
	return t.origin


func _report_wheels(scene: Node, names: Array) -> void:
	for n in names:
		var node := scene.find_child(n, true, false) as Node3D
		var box := AABB()
		var first := true
		for mi in _meshes_of(node):
			var b: AABB = (mi as MeshInstance3D).mesh.get_aabb()
			box = b if first else box.merge(b)
			first = false
		print("  %s at %s, local mesh radius y %.3f z %.3f, half width %.3f" % [n, str(_world(scene, n)), box.size.y / 2, box.size.z / 2, box.size.x / 2])


func _check_rack(scene: Node) -> void:
	var rack := scene.find_child("rack", true, false) as Node3D
	var missile := scene.find_child("missile", true, false) as Node3D
	print("  rack hinge (hull space) ", rack.position, "; missile origin (rack space) ", missile.position)
	var box := AABB()
	for mi in _meshes_of(missile):
		box = (mi as MeshInstance3D).mesh.get_aabb()
	for deg in [0, 45, 90]:
		rack.rotation = Vector3(deg_to_rad(deg), 0, 0)
		var m := rack.transform * missile.transform   # rack is a child of the hull, the missile of the rack
		var nose := m * Vector3(0, 0, box.position.z)
		var tail := m * Vector3(0, 0, box.end.z)
		print("  rack %d deg: nose (hull space) %s, tail %s" % [deg, str(nose), str(tail)])
	rack.rotation = Vector3.ZERO


func _named(n: Node, prefixes: Array) -> Array:
	var out: Array = []
	if n == null:
		return out
	for c in n.get_children():
		for p in prefixes:
			if String(c.name).begins_with(p):
				out.append(c.name)
	return out


func _meshes_of(n: Node) -> Array:
	var out: Array = []
	if n is MeshInstance3D:
		out.append(n)
	out.append_array(n.find_children("*", "MeshInstance3D", true, false))
	return out


func _collect(n: Node, parent: Transform3D, out: Array) -> void:
	var xf := parent
	if n is Node3D:
		xf = parent * (n as Node3D).transform
	if n is MeshInstance3D and (n as MeshInstance3D).mesh != null:
		out.append({"mesh": (n as MeshInstance3D).mesh, "xf": xf})
	elif n is ImporterMeshInstance3D and (n as ImporterMeshInstance3D).mesh != null:
		out.append({"mesh": (n as ImporterMeshInstance3D).mesh.get_mesh(), "xf": xf})
	for c in n.get_children():
		_collect(c, xf, out)
