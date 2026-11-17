shader_type spatial;
// A crop plant's material (MapLoader.Crops.cs: made from the model's own StandardMaterial3D), drawn from a MultiMesh. A field has no edge where the plants
// end: over the outer band of the plant radius each plant grows out of its foot at a distance of its own (a hash of where it stands), so the crop thins out
// and shrinks towards the radius, where the last plant is gone. Like the forest's undergrowth (tree_grow), around the camera: plants cast no shadow, so
// no other camera draws them. Keep in step with CropSettings.Grow.
render_mode diffuse_burley, specular_schlick_ggx, CULL_MODE;

uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec4 albedo_colour : source_color = vec4(1.0);
uniform float scissor = -1.0;      // alpha scissor threshold; negative: opaque
uniform float roughness = 1.0;
uniform float metallic = 0.0;
uniform float specular = 0.5;
uniform float vertex_colour = 0.0; // 1: the vertex colour multiplies the albedo (VertexColorUseAsAlbedo)
uniform float vertex_srgb = 0.0;   // 1: it is in sRGB (VertexColorIsSrgb)
// x: radius (m) beyond which no plant stands; y: the band inside it over which they thin out; z: the share of the band one plant takes to grow
uniform vec3 crop_cut = vec3(110.0, 50.0, 0.3);

void vertex() {
	vec3 foot = MODEL_MATRIX[3].xyz; // the instance's origin in the world
	float band = max(crop_cut.y, 0.001);
	float d = distance(foot, CAMERA_POSITION_WORLD);
	float far = d >= crop_cut.x ? 1.0 : clamp((d - (crop_cut.x - band)) / band, 0.0, 1.0);
	// the plant's own threshold: interleaved-gradient noise of its 25 cm cell
	vec2 cell = floor(foot.xz * 4.0 + vec2(0.4137, 0.2719));
	float threshold = fract(52.9829189 * fract(dot(cell, vec2(0.06711056, 0.00583715))));
	float span = max(crop_cut.z, 0.001);
	float g = clamp((span + (1.0 - span) * threshold - far) / span, 0.0, 1.0);
	VERTEX *= g * g * (3.0 - 2.0 * g);

	if (vertex_colour < 0.5) {
		COLOR = vec4(1.0);
	} else if (vertex_srgb > 0.5 && !OUTPUT_IS_SRGB) {
		COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045)));
	}
}

void fragment() {
	vec4 c = texture(albedo_tex, UV) * albedo_colour;
	if (scissor >= 0.0 && c.a < scissor) {
		discard;
	}
	ALBEDO = c.rgb * COLOR.rgb;
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
}
