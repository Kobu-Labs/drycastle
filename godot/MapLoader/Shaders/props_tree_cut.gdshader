shader_type spatial;
// The baked props meshes (the trees and fences of a chunk or a parcel) when pack trees are on: the flat colour material every map mesh has
// (MapLoader.FlatMaterial: vertex colours as albedo, fully rough, no specular), plus the cut. UV2 holds the base point of the tree a vertex belongs
// to, (1e6, 1e6) for a vertex of a fence. A tree whose base is inside the radius is dropped whole (its vertices collapse into one point), and
// in the fade band it is dithered against the pack tree. Not in the sun's shadow pass (an orthographic projection): there the baked tree stays whole
// and casts the shadow for the pack tree standing in its place, which casts none (pack tiles that cast shadows doubled the draw calls).
render_mode diffuse_burley, specular_schlick_ggx; // StandardMaterial3D's defaults

// @include tree_cut

varying flat float far_share;

void vertex() {
	bool sun_shadow = PROJECTION_MATRIX[3][3] == 1.0; // orthographic: the directional light's shadow pass (the player's camera is perspective)
	far_share = UV2.x > 1.0e5 || sun_shadow ? 1.0 : tree_far(UV2, VERTEX.y, MODEL_MATRIX);
	if (far_share <= 0.0) {
		VERTEX = vec3(0.0);
	}
	// VertexColorIsSrgb, as StandardMaterial3D does it
	if (!OUTPUT_IS_SRGB) {
		COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045)));
	}
}

void fragment() {
	if (far_share < 1.0 && tree_noise(FRAGCOORD.xy) < 1.0 - far_share) {
		discard;
	}
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}
