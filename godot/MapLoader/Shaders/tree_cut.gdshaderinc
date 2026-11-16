// Shared by props_tree_cut.gdshader (the baked trees) and pack_tree.gdshader (the pack trees): where one gives way to the other.
// MapLoader.PackTrees.cs sets tree_cut every frame from the streamer's focus; the light's camera in the shadow pass sees the same
// values, which the camera built-ins would not give. Keep in step with PackTreeMath (FarShare, Threshold, IsBaked).

// x, y: the focus on the ground (world x, z); z: radius (m), negative = off; w: fade band inside the radius (m)
global uniform vec4 tree_cut;
// The same for the undergrowth, which has no baked twin and fades out on a radius of its own (shorter than the trees').
global uniform vec4 tree_cut_under;

// A tree's distance from being a pack tree, by the distance of its base point to the focus: 0 inside radius - band, 1 beyond the radius.
// base_xz is the tree's base in the mesh's own space (x, z), y any height: the model matrix takes it to the world. cut is tree_cut or tree_cut_under.
// xy: the base in the world (what tree_threshold hashes: the baked mesh and the pack tile have different model matrices, the world base is the same); z: the far share.
vec3 tree_far(vec2 base_xz, float y, mat4 model, vec4 cut) {
	vec3 w = (model * vec4(base_xz.x, y, base_xz.y, 1.0)).xyz;
	float d = distance(w.xz, cut.xy);
	float band = max(cut.w, 0.001);
	return vec3(w.xz, d >= cut.z ? 1.0 : clamp((d - (cut.z - band)) / band, 0.0, 1.0));
}

// A tree's own threshold in [0, 1): a hash of its world base point, on a 1 m grid shifted off the centimetre lattice (so the two meshes' float
// rounding of the same base never lands either side of a cell edge). Interleaved-gradient noise of the cell.
float tree_threshold(vec2 base_world) {
	vec2 cell = floor(base_world + vec2(0.4137, 0.2719));
	return fract(52.9829189 * fract(dot(cell, vec2(0.06711056, 0.00583715))));
}

// Whether the baked tree stands (true) or the pack tree (false): the whole tree goes at the distance where far reaches its threshold, so over the band
// the trees swap one by one, no pixel is mixed. Both shaders ask this of the same tree_far result, and the baked one keeps what the pack tree drops.
// Far 0 is always pack (threshold >= 0 is never below it), far 1 always baked (threshold < 1). Mirrors PackTreeMath.IsBaked.
bool tree_is_baked(vec3 swap) {
	return swap.z > tree_threshold(swap.xy);
}
