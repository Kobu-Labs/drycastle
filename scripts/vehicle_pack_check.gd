# Step 6 of scripts/build_vehicle_packs.sh: reloads the imported vehicle GLBs the way the game does (GLTFDocument), checks that the parts the
# standardization step needs are separate nodes, that every material name is in materials.json, and prints size, tris and the node tree depth.
#   godot --headless --script scripts/vehicle_pack_check.gd -- <assets-private dir>
# Exit code 1 when a required node or material is missing. Facts per model: scripts/vehicle_model_probe.gd, docs/research/vehicle-models-facts.md.
extends SceneTree

const REQUIRED := {
	"battle-royale/SM_Veh_Truck_01": ["SM_Veh_Truck_Glass_01", "SM_Veh_Truck_SteeringW_01", "SM_Veh_Truck_Trailer_01", "SM_Veh_Truck_Wheel_fl", "SM_Veh_Truck_Wheel_fr",
		"SM_Veh_Truck_Wheel_rl_01", "SM_Veh_Truck_Wheel_rl_02", "SM_Veh_Truck_Wheel_rr_03", "SM_Veh_Truck_Wheel_rr_02"],
	"battle-royale/SM_Veh_Bike_01": ["SM_Veh_Bike_HandleBars_01", "SM_Veh_Bike_Front_Shock_01", "SM_Veh_Bike_Front_Wheel_01", "SM_Veh_Bike_Rear_Shock_01", "SM_Veh_Bike_Rear_Wheel_01"],
	"battle-royale/SM_Veh_Truck_Destroyed_01": [],  # one merged mesh in the pack
	"battle-royale/SM_Veh_Bike_Destroyed_01": [],   # one merged mesh in the pack
	"military/SM_Veh_Car_Small_01": ["SM_Veh_Car_Small_01_Glass", "SM_Veh_Car_Small_01_SteeringW", "SM_Veh_Car_Small_01_Wheel_fl", "SM_Veh_Car_Small_01_Wheel_fr",
		"SM_Veh_Car_Small_01_Wheel_rl", "SM_Veh_Car_Small_01_Wheel_rr"],
	"quadbike/SM_Veh_QuadBike_01": ["SM_Veh_QuadBike_Wheel_fl", "SM_Veh_QuadBike_Wheel_fr", "SM_Veh_QuadBike_Wheel_rl", "SM_Veh_QuadBike_Wheel_rr", "SM_Veh_QuadBike_HandleBar_01",
		"SM_Veh_QuadBike_Cargo_Front_01", "SM_Veh_QuadBike_Cargo_Back_01", "SM_Veh_QuadBike_Shovel_01"],
}


func _init() -> void:
	var priv := String(OS.get_cmdline_user_args()[0]).trim_suffix("/")
	var bad := 0
	for key in REQUIRED:
		var dir: String = String(key).get_base_dir()
		var mats: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("%s/synty/glb/%s/materials.json" % [priv, dir]))["materials"]
		var doc := GLTFDocument.new()
		var state := GLTFState.new()
		if doc.append_from_file("%s/synty/glb/%s.glb" % [priv, key], state) != OK:
			print("FAIL load ", key)
			bad += 1
			continue
		var scene: Node = doc.generate_scene(state)
		for n in REQUIRED[key]:
			if scene.find_child(n, true, false) == null:
				print("MISSING node ", key, "/", n)
				bad += 1
		var meshes: Array = []
		_collect(scene, Transform3D.IDENTITY, meshes)
		var box := AABB()
		var first := true
		var tris := 0
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
				if mat != null and mat.resource_name != "" and not mats.has(mat.resource_name):
					print("NO MATERIAL ENTRY ", key, " ", mat.resource_name)
					bad += 1
		print("%s: %d meshes, %d tris, size %.2f x %.2f x %.2f m (loose bounds)" % [key, meshes.size(), tris, box.size.x, box.size.y, box.size.z])
		scene.free()
	print("check: ", "OK" if bad == 0 else "%d problem(s)" % bad)
	quit(1 if bad > 0 else 0)


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
