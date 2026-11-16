shader_type spatial;
// One material of the pack's trees (materials.json: albedo texture and colour, alpha scissor, cull mode, roughness), drawn from a tile mesh that holds
// many trees. UV2 holds the base point of the tree a vertex belongs to (PackTileMesher). A tree whose base is beyond the radius is dropped whole
// (its vertices collapse into one point), and in the fade band it is dithered against the baked tree. MapLoader.PackTrees.cs replaces CULL_MODE.
render_mode diffuse_burley, specular_schlick_ggx, CULL_MODE;

// @include tree_cut

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec4 albedo_colour : source_color = vec4(1.0);
uniform float scissor = -1.0;   // alpha scissor threshold; negative: opaque
uniform float roughness = 0.8;
uniform float metallic = 0.0;
uniform float specular = 0.5;

varying flat float far_share;

void vertex() {
	far_share = tree_far(UV2, VERTEX.y, MODEL_MATRIX);
	if (far_share >= 1.0) {
		VERTEX = vec3(0.0);
	}
}

void fragment() {
	if (far_share > 0.0 && tree_noise(FRAGCOORD.xy) >= 1.0 - far_share) {
		discard;
	}
	vec4 c = texture(albedo_tex, UV) * albedo_colour;
	if (scissor >= 0.0 && c.a < scissor) {
		discard;
	}
	ALBEDO = c.rgb;
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
}
