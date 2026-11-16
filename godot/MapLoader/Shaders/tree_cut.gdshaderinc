// Shared by props_tree_cut.gdshader (the baked trees) and pack_tree.gdshader (the pack trees): where one gives way to the other.
// MapLoader.PackTrees.cs sets tree_cut every frame from the streamer's focus; the light's camera in the shadow pass sees the same
// values, which the camera built-ins would not give. Keep in step with PackTreeMath.FarShare.

// x, y: the focus on the ground (world x, z); z: radius (m), negative = off; w: fade band inside the radius (m)
global uniform vec4 tree_cut;
// The same for the undergrowth, which has no baked twin and fades out on a radius of its own (shorter than the trees').
global uniform vec4 tree_cut_under;

// A tree's distance from being a pack tree, by the distance of its base point to the focus: 0 inside radius - band, 1 beyond the radius.
// base_xz is the tree's base in the mesh's own space (x, z), y any height: the model matrix takes it to the world. cut is tree_cut or tree_cut_under.
float tree_far(vec2 base_xz, float y, mat4 model, vec4 cut) {
	vec3 w = (model * vec4(base_xz.x, y, base_xz.y, 1.0)).xyz;
	float d = distance(w.xz, cut.xy);
	float band = max(cut.w, 0.001);
	return d >= cut.z ? 1.0 : clamp((d - (cut.z - band)) / band, 0.0, 1.0);
}

// Screen-door noise in [0, 1) at a pixel. Both sides use the same one: the pack tree keeps the pixels where it is below 1 - far, the baked tree the rest.
float tree_noise(vec2 frag) {
	return fract(52.9829189 * fract(dot(frag, vec2(0.06711056, 0.00583715))));
}
