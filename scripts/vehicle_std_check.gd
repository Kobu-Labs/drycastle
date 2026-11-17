# Reloads the standardized vehicle GLBs (scripts/standardize_vehicle.py) the way the game does and checks the contract of docs/standards/car-design.md part A:
# hull is a direct child of the root, wheels are its siblings (Position.Y = radius, a _steer parent on the steered ones), glass_N / seat_* / steering_wheel_* / light_*
# nodes are under the hull, no collision bodies, every material name is in the pack's materials.json, the bounds are the facts file's.
#   godot --headless --script scripts/vehicle_std_check.gd -- <assets-private dir> <facts.json> [<facts.json> ...]     (docs/research/vehicle-facts.d/<id>.json)
# Exit code 1 on any problem.
extends SceneTree

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var priv := String(args[0]).trim_suffix("/")
	var bad := 0
	for fi in range(1, args.size()):
		var facts: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(args[fi]))
		var dir: String = facts["out_dir"]
		var mats: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("%s/%s/materials.json" % [priv, dir]))["materials"]
		for skin in facts["skins"]:
			var name := String(skin["model"]).get_slice("/", 1)
			var doc := GLTFDocument.new()
			var state := GLTFState.new()
			if doc.append_from_file("%s/%s/%s.glb" % [priv, dir, name], state) != OK:
				print("FAIL load ", name)
				bad += 1
				continue
			var root: Node = doc.generate_scene(state)
			bad += _check(name, root, facts, mats)
			root.free()
	print("check: ", "OK" if bad == 0 else "%d problem(s)" % bad)
	quit(1 if bad > 0 else 0)

func _check(name: String, root: Node, facts: Dictionary, mats: Dictionary) -> int:
	var bad := 0
	var hull: Node = root.find_child("hull", true, false)
	if hull == null:
		print("MISSING hull in ", name)
		return 1
	root = hull.get_parent() # the vehicle's own node (the importer may add a wrapper above it)
	for n in facts["nodes"]:
		if n != root.name and root.find_child(n, true, false) == null:
			print("MISSING node ", name, "/", n)
			bad += 1
	if not root.find_children("*", "CollisionObject3D", true, false).is_empty():
		print("COLLISION BODY left in ", name)
		bad += 1
	for wn in facts["wheels"]["centres"]:
		var w: Node3D = root.find_child("wheel_" + wn, true, false)
		var radius: float = facts["wheels"]["centres"][wn]["radius"]
		var steer: Node3D = root.find_child("wheel_%s_steer" % wn, true, false)
		if w == null:
			continue
		if hull.is_ancestor_of(w) or w.get_parent() != (steer if steer != null else root):
			print("WHEEL not a sibling of hull: ", name, "/", wn)
			bad += 1
		var y: float = _height_in(w, root) # through the steer parent (a tilted steering axis turns the child's own Y)
		if absf(y - radius) > 0.005:
			print("WHEEL height ", name, "/", wn, " y ", y, " radius ", radius)
			bad += 1
		if bool(facts["wheels"]["centres"][wn]["steer"]) != (steer != null):
			print("WHEEL steer parent mismatch ", name, "/", wn)
			bad += 1
	for wn in facts["wheels"]["centres"]:
		var wc: Dictionary = facts["wheels"]["centres"][wn]
		if wc.has("steer_axis"):
			var sp: Node3D = root.find_child("wheel_%s_steer" % wn, true, false)
			var ax: Array = wc["steer_axis"]
			var got: Vector3 = sp.transform.basis.y.normalized()
			if got.distance_to(Vector3(ax[0], ax[1], ax[2])) > 0.01 or not is_equal_approx(sp.transform.basis.x.y, 0.0):
				print("STEER AXIS of wheel_%s_steer is %s, facts %s" % [wn, got, ax])
				bad += 1
	for cn in facts.get("controls", {}):
		var cd: Dictionary = facts["controls"][cn]
		var cnode: Node3D = root.find_child(cn, true, false)
		var up: String = cd["under"]
		var par: Node = cnode.get_parent() if cnode != null else null
		if cnode == null or par == null or par.name != up:
			print("CONTROL %s not under %s in %s" % [cn, up, name])
			bad += 1
	for i in range(facts["glass"].size()):
		var g := root.find_child("glass_%d" % i, true, false)
		if g == null or not hull.is_ancestor_of(g):
			print("glass_%d not under hull in %s" % [i, name])
			bad += 1
	for n in facts["nodes"]:
		if (String(n).begins_with("seat_") or String(n).begins_with("steering_wheel_") or String(n).begins_with("light_")):
			var nd := root.find_child(n, true, false)
			if nd != null and not hull.is_ancestor_of(nd):
				print("not under hull: ", name, "/", n)
				bad += 1
	var box := AABB()
	var first := true
	var tris := 0
	var meshes: Array = []
	_collect(root.get_parent() if root.get_parent() != null else root, Transform3D.IDENTITY, meshes)
	for e in meshes:
		var m: Mesh = e["mesh"]
		var xf: Transform3D = e["xf"]
		for p in m.get_faces(): # by vertex: the AABB of a rotated box is loose (a raked fork)
			var q: Vector3 = xf * p
			box = AABB(q, Vector3.ZERO) if first else box.expand(q)
			first = false
		for s in m.get_surface_count():
			tris += m.surface_get_array_index_len(s) / 3
			var mat := m.surface_get_material(s)
			if mat != null and mat.resource_name != "" and not mats.has(mat.resource_name) and not String(mat.resource_name).begins_with("Emissive") and mat.resource_name != "Transparent blue glass":
				print("NO MATERIAL ENTRY ", name, " ", mat.resource_name)
				bad += 1
	var lo: Array = facts["bounds"]["min"]
	var hi: Array = facts["bounds"]["max"]
	var want := Vector3(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2])
	if (box.size - want).length() > 0.02 or absf(box.position.y - lo[1]) > 0.02:
		print("BOUNDS %s: got %s at y %.3f, facts %s" % [name, box.size, box.position.y, want])
		bad += 1
	print("%s: %d tris, %.2f x %.2f x %.2f m, y %.3f..%.3f" % [name, tris, box.size.x, box.size.y, box.size.z, box.position.y, box.end.y])
	return bad

## The height of a node's origin in the frame of `root` (the nodes are not in a tree, so no global transform): the product of the transforms up to it.
func _height_in(n: Node3D, root: Node) -> float:
	var xf := Transform3D.IDENTITY
	var cur: Node = n
	while cur != null and cur != root:
		xf = (cur as Node3D).transform * xf
		cur = cur.get_parent()
	return xf.origin.y

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
