# Step 2 of the SIMPLE Military vehicle batch (scripts/military_kit/build_simple_military.sh): re-exports each imported pack FBX as a plain GLB
# (Godot's own FBX importer does the units and axes), so the model scripts can read the pack's meshes with a small glTF reader.
#   godot --headless --path assets-private/synty/simple-military --script <repo>/scripts/military_kit/simple_military_parts.gd -- [dump]
# Writes parts/<Name>.glb for every fbx/*.fbx (name-only materials: the pack's texture is NOT in them). With "dump" it only prints the node
# tree: local transform, mesh bounds, surfaces, vertices.
extends SceneTree


func _init() -> void:
	var dump := OS.get_cmdline_user_args().has("dump")
	DirAccess.make_dir_recursive_absolute("res://parts")
	var files := Array(DirAccess.get_files_at("res://fbx"))
	files.sort()
	for f in files:
		if not String(f).ends_with(".fbx"):
			continue
		var scene := (load("res://fbx/" + f) as PackedScene).instantiate()
		if dump:
			print("== ", f)
			_dump(scene, 0)
		else:
			var doc := GLTFDocument.new()
			var state := GLTFState.new()
			doc.append_from_scene(scene, state)
			var out := ProjectSettings.globalize_path("res://parts/" + String(f).get_basename() + ".glb")
			var err := doc.write_to_filesystem(state, out)
			print("parts/", String(f).get_basename(), ".glb ", "ok" if err == OK else "FAILED %d" % err)
		scene.free()
	quit()


func _dump(n: Node, depth: int) -> void:
	var line := "  ".repeat(depth) + n.name + " [" + n.get_class() + "]"
	if n is Node3D:
		var t: Transform3D = n.transform
		line += " pos=(%.4f %.4f %.4f) rot=(%.1f %.1f %.1f) scale=(%.3f %.3f %.3f)" % [t.origin.x, t.origin.y, t.origin.z,
			rad_to_deg(t.basis.get_euler().x), rad_to_deg(t.basis.get_euler().y), rad_to_deg(t.basis.get_euler().z), t.basis.get_scale().x, t.basis.get_scale().y, t.basis.get_scale().z]
	if n is MeshInstance3D and n.mesh != null:
		var b: AABB = n.mesh.get_aabb()
		var verts := 0
		var mats: Array = []
		for s in n.mesh.get_surface_count():
			verts += (n.mesh.surface_get_arrays(s)[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()
			var m: Material = n.mesh.surface_get_material(s)
			mats.append(m.resource_name if m != null else "-")
		line += " aabb=(%.3f %.3f %.3f)..(%.3f %.3f %.3f) surfaces=%d verts=%d mats=%s" % [b.position.x, b.position.y, b.position.z, b.end.x, b.end.y, b.end.z, n.mesh.get_surface_count(), verts, str(mats)]
	print(line)
	for c in n.get_children():
		_dump(c, depth + 1)
