extends SceneTree
## Headless import check for the asset library.
##
## godot --headless --path godot/viewer --script res://asset_check.gd -- --manifest <manifest.json> [--json out.json]
## godot --headless --path godot/viewer --script res://asset_check.gd -- --files a.glb b.gltf ...
##
## Loads every glTF/GLB with GLTFDocument at runtime, prints one line per file (OK/FAIL, node and
## triangle counts, bounding box, material kind) and exits 1 if any file failed to load.
## Material kinds: "flat" (albedo colour only), "vertex" (vertex colours), "textured" (an albedo
## or other texture is referenced), "mixed" (flat and textured surfaces).

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var files: Array = []  # of {id, path}
	var json_out := ""
	var i := 0
	while i < args.size():
		match args[i]:
			"--manifest":
				i += 1
				files += _from_manifest(args[i])
			"--files":
				while i + 1 < args.size() and not args[i + 1].begins_with("--"):
					i += 1
					files.append({"id": args[i].get_file(), "path": args[i]})
			"--json":
				i += 1
				json_out = args[i]
		i += 1
	if files.is_empty():
		printerr("asset_check: nothing to check (use --manifest or --files)")
		quit(2)
		return
	var results: Array = []
	var failed := 0
	for f in files:
		var r := _check(f.path)
		r["id"] = f.id
		r["file"] = f.path
		results.append(r)
		if not r.ok:
			failed += 1
		var line := "%s  %s" % ["OK  " if r.ok else "FAIL", f.id]
		if r.ok:
			line += "  nodes=%d meshes=%d tris=%d size=[%.3f %.3f %.3f] material=%s" % [
				r.nodes, r.meshes, r.tris, r.size[0], r.size[1], r.size[2], r.material]
			if not r.textures.is_empty():
				line += " textures=" + ",".join(r.textures)
		else:
			line += "  " + r.error
		print(line)
	print("asset_check: %d files, %d ok, %d failed" % [results.size(), results.size() - failed, failed])
	if json_out != "":
		var fa := FileAccess.open(json_out, FileAccess.WRITE)
		fa.store_string(JSON.stringify(results, "  ", true))
		fa.close()
	quit(1 if failed > 0 else 0)


func _from_manifest(path: String) -> Array:
	var doc = JSON.parse_string(FileAccess.get_file_as_string(path))
	var out: Array = []
	if typeof(doc) != TYPE_DICTIONARY:
		printerr("asset_check: cannot parse manifest ", path)
		return out
	var base := path.get_base_dir()
	for e in doc.get("assets", []):
		if e.get("file") != null:
			out.append({"id": e["id"], "path": base.path_join(e["file"])})
	return out


func _check(path: String) -> Dictionary:
	var state := GLTFState.new()
	var doc := GLTFDocument.new()
	var err := doc.append_from_file(path, state)
	if err != OK:
		return {"ok": false, "error": "append_from_file: %s" % error_string(err)}
	var root := doc.generate_scene(state)
	if root == null:
		return {"ok": false, "error": "generate_scene returned null"}
	var info := {"ok": true, "nodes": 0, "meshes": 0, "tris": 0, "textures": [], "kinds": {}}
	var box := [AABB(), false]
	_walk(root, Transform3D.IDENTITY, info, box)
	var aabb: AABB = box[0]
	info["size"] = [aabb.size.x, aabb.size.y, aabb.size.z]
	info["min"] = [aabb.position.x, aabb.position.y, aabb.position.z]
	var kinds: Dictionary = info["kinds"]
	if kinds.is_empty():
		info["material"] = "none"
	elif kinds.size() > 1:
		info["material"] = "mixed"
	else:
		info["material"] = kinds.keys()[0]
	info.erase("kinds")
	root.free()
	return info


func _walk(n: Node, parent_xf: Transform3D, info: Dictionary, box: Array) -> void:
	info["nodes"] += 1
	var xf := parent_xf
	if n is Node3D:
		xf = parent_xf * (n as Node3D).transform
	if n is MeshInstance3D and (n as MeshInstance3D).mesh != null:
		var mi := n as MeshInstance3D
		info["meshes"] += 1
		var mesh := mi.mesh
		for s in mesh.get_surface_count():
			var arrays := mesh.surface_get_arrays(s)
			var idx = arrays[Mesh.ARRAY_INDEX]
			var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			info["tris"] += (idx.size() if idx != null and idx.size() > 0 else verts.size()) / 3
			var mat := mesh.surface_get_material(s)
			var kind := "flat"
			if mat is BaseMaterial3D:
				var bm := mat as BaseMaterial3D
				var texs := {
					"albedo": bm.albedo_texture, "normal": bm.normal_texture,
					"emission": bm.emission_texture, "orm": bm.orm_texture,
					"metallic": bm.metallic_texture, "roughness": bm.roughness_texture}
				var tex_names: Array = []
				for k in texs:
					if texs[k] != null:
						tex_names.append(k)
				if not tex_names.is_empty():
					kind = "textured"
					for t in tex_names:
						if not info["textures"].has(t):
							info["textures"].append(t)
				elif bm.vertex_color_use_as_albedo:
					kind = "vertex"
			elif arrays[Mesh.ARRAY_COLOR] != null:
				kind = "vertex"
			info["kinds"][kind] = true
		var local := mesh.get_aabb()
		var w := xf * local
		if box[1]:
			box[0] = (box[0] as AABB).merge(w)
		else:
			box[0] = w
			box[1] = true
	for c in n.get_children():
		_walk(c, xf, info, box)
