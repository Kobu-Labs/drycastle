# Measures vehicle GLBs the way the game loads them (GLTFDocument) for docs/research/vehicle-models-facts.md: node tree with local and world
# position of each node, per-mesh world bounds / centre / surfaces / tris / material names, total bounds and tris.
#   godot --headless --script scripts/vehicle_model_probe.gd -- [--tris] <a.glb> [<b.glb> ...]
extends SceneTree

var _dump_small := false


func _init() -> void:
	for path in OS.get_cmdline_user_args():
		if path == "--tris":  # also list every triangle of meshes with at most 80 (glass panes)
			_dump_small = true
			continue
		var doc := GLTFDocument.new()
		var state := GLTFState.new()
		if doc.append_from_file(path, state) != OK:
			print("FAIL load ", path)
			continue
		var scene: Node = doc.generate_scene(state)
		print("== ", path)
		var tot := [AABB(), true, 0]
		_walk(scene, Transform3D.IDENTITY, 0, tot)
		var b: AABB = tot[0]
		print("TOTAL size (%.3f %.3f %.3f) min (%.3f %.3f %.3f) max (%.3f %.3f %.3f) tris %d" % [b.size.x, b.size.y, b.size.z, b.position.x, b.position.y, b.position.z, b.end.x, b.end.y, b.end.z, tot[2]])
		scene.free()
	quit()


func _walk(n: Node, parent: Transform3D, depth: int, tot: Array) -> void:
	var xf := parent
	var line := "  ".repeat(depth) + n.name + " [" + n.get_class() + "]"
	if n is Node3D:
		xf = parent * (n as Node3D).transform
		var p: Vector3 = (n as Node3D).position
		line += " local(%.3f %.3f %.3f) world(%.3f %.3f %.3f)" % [p.x, p.y, p.z, xf.origin.x, xf.origin.y, xf.origin.z]
		var sc: Vector3 = (n as Node3D).transform.basis.get_scale()
		if absf(sc.x - 1.0) > 0.001 or absf(sc.y - 1.0) > 0.001 or absf(sc.z - 1.0) > 0.001:
			line += " scale(%.3f %.3f %.3f)" % [sc.x, sc.y, sc.z]
		var e: Vector3 = (n as Node3D).transform.basis.get_euler() * 57.29578
		if absf(e.x) + absf(e.y) + absf(e.z) > 0.05:
			line += " rot(%.1f %.1f %.1f)" % [e.x, e.y, e.z]
	var m: Mesh = null
	if n is MeshInstance3D:
		m = (n as MeshInstance3D).mesh
	elif n is ImporterMeshInstance3D and (n as ImporterMeshInstance3D).mesh != null:
		m = (n as ImporterMeshInstance3D).mesh.get_mesh()
	if m != null:
		var b := AABB()  # tight: the vertices through the node's world transform (a rotated parent would inflate xf * aabb)
		var first := true
		var tris := 0
		var mats: Array = []
		var la := m.get_aabb()
		var uv_text := ""
		for s in m.get_surface_count():
			var a := m.surface_get_arrays(s)
			var idx = a[Mesh.ARRAY_INDEX]
			tris += (idx.size() if idx != null else (a[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()) / 3
			var uvs = a[Mesh.ARRAY_TEX_UV]
			if uvs != null and (uvs as PackedVector2Array).size() > 0:
				var u0: Vector2 = (uvs as PackedVector2Array)[0]
				var ub := Rect2(u0, Vector2.ZERO)
				for uv in (uvs as PackedVector2Array):
					ub = ub.expand(uv)
				uv_text += " uv(%.3f..%.3f, %.3f..%.3f)" % [ub.position.x, ub.end.x, ub.position.y, ub.end.y]
			for v in (a[Mesh.ARRAY_VERTEX] as PackedVector3Array):
				var w := xf * v
				if first:
					b = AABB(w, Vector3.ZERO)
					first = false
				else:
					b = b.expand(w)
			if tris <= 80 and _dump_small:
				var vs: PackedVector3Array = a[Mesh.ARRAY_VERTEX]
				var ii: PackedInt32Array = idx if idx != null else PackedInt32Array()
				for t in range(0, ii.size(), 3):
					var p0 := xf * vs[ii[t]]
					var p1 := xf * vs[ii[t + 1]]
					var p2 := xf * vs[ii[t + 2]]
					var nn := (p1 - p0).cross(p2 - p0).normalized()
					print("  ".repeat(depth + 1), "tri n(%.2f %.2f %.2f) centre(%.3f %.3f %.3f) p0(%.3f %.3f %.3f) p1(%.3f %.3f %.3f) p2(%.3f %.3f %.3f)" % [nn.x, nn.y, nn.z, (p0 + p1 + p2).x / 3, (p0 + p1 + p2).y / 3, (p0 + p1 + p2).z / 3, p0.x, p0.y, p0.z, p1.x, p1.y, p1.z, p2.x, p2.y, p2.z])
			var mat := m.surface_get_material(s)
			mats.append(mat.resource_name if mat != null else "-")
		var c := b.get_center()
		line += " localAabb(%.3f..%.3f, %.3f..%.3f, %.3f..%.3f) bbox(%.3f..%.3f, %.3f..%.3f, %.3f..%.3f) size(%.3f %.3f %.3f) centre(%.3f %.3f %.3f) surf %d tris %d mats %s%s" % [la.position.x, la.end.x, la.position.y, la.end.y, la.position.z, la.end.z, b.position.x, b.end.x, b.position.y, b.end.y, b.position.z, b.end.z, b.size.x, b.size.y, b.size.z, c.x, c.y, c.z, m.get_surface_count(), tris, str(mats), uv_text]
		tot[0] = b if tot[1] else (tot[0] as AABB).merge(b)
		tot[1] = false
		tot[2] += tris
	print(line)
	for c in n.get_children():
		_walk(c, xf, depth + 1, tot)
