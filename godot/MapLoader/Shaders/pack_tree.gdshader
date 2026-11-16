shader_type spatial;
// One material of the pack's trees (materials.json: albedo texture and colour, alpha scissor, cull mode, roughness), drawn from a tile mesh that holds
// many trees. UV2 holds the base point of the tree a vertex belongs to (PackTileMesher). A tree whose base is beyond the radius is dropped whole
// (its vertices collapse into one point), and in the fade band it is dithered against the baked tree. MapLoader.PackTrees.cs replaces CULL_MODE.
// COLOR (PackTileMesher): rgb = the tree's foliage tint halved, a = what it is: 1 tree, 0.5 ground object (cut like a tree, no tint), 0 undergrowth
// (own radius, tree_cut_under: nothing stands behind it, so it just dithers out).
render_mode diffuse_burley, specular_schlick_ggx, CULL_MODE;

// @include tree_cut

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec4 albedo_colour : source_color = vec4(1.0);
uniform float scissor = -1.0;   // alpha scissor threshold; negative: opaque
uniform float roughness = 0.8;
uniform float metallic = 0.0;
uniform float specular = 0.5;
// Foliage colour (pack_trees.json colour): saturation and brightness of the green texels, and how green a texel must be to count as foliage.
uniform float foliage_saturation = 1.0;
uniform float foliage_gain = 1.0;
uniform float leaf_lo = 0.06;
uniform float leaf_hi = 0.3;
// Foliage takes the colour in the vertex colour (the baked tree's own green), shaded by the texel's brightness against leaf_ref.
uniform float recolour = 1.0;
uniform float leaf_ref = 0.2;
uniform float shade_min = 0.75;
uniform float shade_max = 1.25;

varying flat float far_share;
varying flat vec3 tint;
varying flat float tinted;

void vertex() {
	float kind = COLOR.a;
	far_share = tree_far(UV2, VERTEX.y, MODEL_MATRIX, kind < 0.25 ? tree_cut_under : tree_cut);
	if (far_share >= 1.0) {
		VERTEX = vec3(0.0);
	}
	tint = COLOR.rgb * 2.0;
	tinted = abs(kind - 0.5) > 0.25 ? 1.0 : 0.0; // ground objects keep the atlas as it is
}

void fragment() {
	if (far_share > 0.0 && tree_noise(FRAGCOORD.xy) >= 1.0 - far_share) {
		discard;
	}
	vec4 c = texture(albedo_tex, UV) * albedo_colour;
	if (scissor >= 0.0 && c.a < scissor) {
		discard;
	}
	// foliage only: by how much greener than red the texel is (the atlas' leaves run from olive to blue-grey), so brown trunks and bark keep their colour
	float leaf = tinted * smoothstep(leaf_lo, leaf_hi, (c.g - c.r) / max(c.g, 0.01));
	vec3 lum = vec3(0.2126, 0.7152, 0.0722);
	vec3 varied = recolour > 0.5 ? tint * clamp(dot(c.rgb, lum) / leaf_ref, shade_min, shade_max) : c.rgb * tint;
	float luma = dot(varied, lum);
	varied = mix(vec3(luma), varied, foliage_saturation) * foliage_gain;
	ALBEDO = mix(c.rgb, varied, leaf);
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
}
