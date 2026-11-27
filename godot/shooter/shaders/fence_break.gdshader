// Fence destruction (docs/fence-destruction.md, WP-V): a thin fence's textured panel with its broken pieces cut out.
// The look is MapLoader.TextureMaterial's: the library texture (repeating, mipmapped, anisotropic) times the sRGB vertex colour, fully rough, no metal, no specular,
// alpha-tested at 0.4 with alpha to coverage (edge 0.3) so the cut-outs of pickets and wire keep their shape under multisampling (an opaque texture passes the test).
// A fragment is discarded when its piece is broken. TEXCOORD_1 (UV2) of every vertex is (section index + 0.5, piece position along the section): floor of y is the piece.
// mask is an R8 texture, one texel a piece (255 = broken): column piece + 64 * (section / 16384), row section % 16384 (FenceWorldNode).
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx, alpha_to_coverage;

const int MASK_COLUMNS = 64;
const int MASK_ROWS = 16384;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mask : filter_nearest, repeat_disable;

void vertex() {
	// VertexColorIsSrgb: the colours are sRGB, the lighting is linear
	COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045)));
}

void fragment() {
	int section = int(UV2.x);
	int piece = clamp(int(floor(UV2.y)), 0, MASK_COLUMNS - 1);
	if (texelFetch(mask, ivec2(piece + MASK_COLUMNS * (section / MASK_ROWS), section % MASK_ROWS), 0).r > 0.5) {
		discard;
	}
	vec4 tex = texture(albedo_tex, UV);
	ALBEDO = COLOR.rgb * tex.rgb;
	ALPHA = COLOR.a * tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.4;
	ALPHA_ANTIALIASING_EDGE = 0.3;
	ALPHA_TEXTURE_COORDINATE = UV * vec2(textureSize(albedo_tex, 0));
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}
