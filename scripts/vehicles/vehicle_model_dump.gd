# Dumps every mesh of a vehicle GLB (world space, as the game loads it) to JSON for the numpy analysis of docs/research/vehicle-models-facts.md
# (glass panes, interior, skins): {"nodes":[{"name","path","origin":[x,y,z],"mats":[...],"v":[x,y,z,...],"i":[...],"uv":[u,v,...],"n":[...]}]}.
#   godot --headless --script scripts/vehicle_model_dump.gd -- <in.glb> <out.json>
extends SceneTree


func _init() -> void:
	var a := OS.get_cmdline_user_args()
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	if doc.append_from_file(a[0], state) != OK:
		quit(1)
		return
	var scene: Node = doc.generate_scene(state)
	var out: Array = []
	_walk(scene, Transform3D.IDENTITY, "", out)
	var f := FileAccess.open(a[1], FileAccess.WRITE)
	f.store_string(JSON.stringify({"nodes": out}))
	f.close()
	scene.free()
	quit()


func _walk(n: Node, parent: Transform3D, path: String, out: Array) -> void:
	var xf := parent
	if n is Node3D:
		xf = parent * (n as Node3D).transform
	var p := path + "/" + String(n.name)
	var m: Mesh = null
	if n is MeshInstance3D:
		m = (n as MeshInstance3D).mesh
	elif n is ImporterMeshInstance3D and (n as ImporterMeshInstance3D).mesh != null:
		m = (n as ImporterMeshInstance3D).mesh.get_mesh()
	if m != null:
		var v: Array = []
		var idx: Array = []
		var uv: Array = []
		var mats: Array = []
		var surf_start: Array = []
		var base := 0
		for s in m.get_surface_count():
			var arr := m.surface_get_arrays(s)
			var vs: PackedVector3Array = arr[Mesh.ARRAY_VERTEX]
			var uvs = arr[Mesh.ARRAY_TEX_UV]
			var ii = arr[Mesh.ARRAY_INDEX]
			for i in vs.size():
				var w := xf * vs[i]
				v.append_array([snappedf(w.x, 0.0001), snappedf(w.y, 0.0001), snappedf(w.z, 0.0001)])
				if uvs != null:
					uv.append_array([snappedf((uvs as PackedVector2Array)[i].x, 0.0001), snappedf((uvs as PackedVector2Array)[i].y, 0.0001)])
			surf_start.append(idx.size())
			if ii != null:
				for k in (ii as PackedInt32Array):
					idx.append(k + base)
			else:
				for k in vs.size():
					idx.append(k + base)
			base += vs.size()
			var mat := m.surface_get_material(s)
			mats.append(mat.resource_name if mat != null else "")
		out.append({"name": String(n.name), "path": p, "origin": [xf.origin.x, xf.origin.y, xf.origin.z], "mats": mats, "surf_start": surf_start, "v": v, "i": idx, "uv": uv})
	for c in n.get_children():
		_walk(c, xf, p, out)
