# Converts the FBX props of the Synty POLYGON Construction pack that scripts/packs/construction_pack_glb.py put in a scratch Godot project into the runtime GLBs the
# mechanic garage (godot/shooter/MechanicGarage.cs) reads, and shrinks the pack's atlas to 1024 px. Geometry and UVs only: SyntyLib puts the atlas on the model.
#
#   godot --headless --path <scratch project> --script construction_pack_glb.gd -- <assets-private dir>
#
# Writes <assets-private>/synty/glb/construction/<Model>.glb and <assets-private>/synty/glb/atlas/construction_01_A.png. The pack is paid and never enters the repository.
extends SceneTree

const MODELS := "res://Assets/Synty/PolygonConstruction/Models"
const ATLAS := "res://Assets/Synty/PolygonConstruction/Textures/PolygonConstruction_Texture_01_A.png"

func _init() -> void:
	var out := String(OS.get_cmdline_user_args()[0]).trim_suffix("/")
	var models := "%s/synty/glb/construction" % out
	DirAccess.make_dir_recursive_absolute(models)
	DirAccess.make_dir_recursive_absolute("%s/synty/glb/atlas" % out)
	var failed := 0
	var blank := StandardMaterial3D.new()
	blank.resource_name = "Synty PolygonConstruction"
	var dir := DirAccess.open(MODELS)
	for f in dir.get_files():
		if not f.ends_with(".fbx"):
			continue
		var packed := load("%s/%s" % [MODELS, f]) as PackedScene
		if packed == null:
			push_error("cannot import " + f)
			failed += 1
			continue
		var node := packed.instantiate()
		_strip(node, blank)
		var root := Node3D.new()
		root.name = "Prop"
		root.add_child(node)
		_own(root, root)
		var doc := GLTFDocument.new()
		var st := GLTFState.new()
		var err := doc.append_from_scene(root, st)
		if err == OK:
			err = doc.write_to_filesystem(st, "%s/%s.glb" % [models, f.get_basename()])
		if err != OK:
			push_error("export failed " + f)
			failed += 1
		root.free()
	var img := Image.load_from_file(ProjectSettings.globalize_path(ATLAS))
	if img == null:
		push_error("no atlas")
		failed += 1
	else:
		img.resize(1024, 1024, Image.INTERPOLATE_LANCZOS)
		img.save_png("%s/synty/glb/atlas/construction_01_A.png" % out)
	print("converted, failed=", failed)
	quit(1 if failed > 0 else 0)

func _own(n: Node, root: Node) -> void:
	for c in n.get_children():
		c.owner = root
		_own(c, root)

func _strip(n: Node, mat: Material) -> void:
	if n is MeshInstance3D:
		var mi := n as MeshInstance3D
		if mi.get_node_or_null(mi.skeleton) as Skeleton3D == null:
			mi.skin = null
		mi.material_override = mat
	for c in n.get_children():
		_strip(c, mat)
