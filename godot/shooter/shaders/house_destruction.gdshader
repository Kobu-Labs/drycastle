// House destruction (docs/house-destruction.md, WP-F): a hollow house's smooth mesh with its carved cells cut out.
// The look is the map's flat material (MapLoader FlatMaterial): sRGB vertex colours as albedo, fully rough, no metal, no specular.
// A fragment is discarded when its cell in the house's hidden grid is removed: the world position, stepped 0.01 m against the normal
// (a face on a cell boundary reads the cell behind it), is mapped to grid cells by world_to_grid and looks up an R8 3D texture
// (one texel a cell, nearest, 1 = hidden: near a hole, where voxel blocks are drawn instead, or air left with nothing next to it;
// ShooterCore HouseDamageLook). Outside the grid nothing is discarded.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

uniform mat4 world_to_grid;
uniform vec3 grid_size = vec3(1.0);
uniform highp sampler3D removed : filter_nearest, repeat_disable;

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	// VertexColorIsSrgb: the colours are sRGB, the lighting is linear
	COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045)));
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

void fragment() {
	vec3 g = (world_to_grid * vec4(world_pos - normalize(world_normal) * 0.01, 1.0)).xyz;
	if (all(greaterThanEqual(g, vec3(0.0))) && all(lessThan(g, grid_size))) {
		if (texture(removed, g / grid_size).r > 0.5) {
			discard;
		}
	}
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}
