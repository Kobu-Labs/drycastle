shader_type spatial;
// The far look of a crop field (MapLoader.CropSheet.cs): a flat sheet over the planted ground, a little above it, with a tiling picture of the crop seen from
// above (painted from the plant models: colour, alpha = where a plant is). It is blended in over the band where the plants thin out (crop_plant.gdshader) and
// stays to the horizon, so a field far away looks planted without a single plant drawn. Blended, not cut out: far away the picture's mipmaps turn the plants
// into a tint of their colour over the ground, which is what a far field is. COLOR.a: how full of plants the ground is there (the field's edge feathers).
render_mode blend_mix, depth_draw_never, cull_disabled, diffuse_burley, specular_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// x: the plants' radius (m); y: the band inside it over which they thin out and the sheet comes in
uniform vec3 crop_cut = vec3(110.0, 50.0, 0.3);
// Seen at a low angle a crop hides more of the ground than from above (the plants stand): the cover is taken through this much more crop at most.
uniform float slant_max = 6.0;

varying vec3 world;

void vertex() {
	world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec4 c = texture(albedo_tex, UV);
	vec3 to_eye = CAMERA_POSITION_WORLD - world;
	float d = length(to_eye);
	float band = max(crop_cut.y, 0.001);
	float share = d >= crop_cut.x ? 1.0 : clamp((d - (crop_cut.x - band)) / band, 0.0, 1.0);
	float through = min(1.0 / max(abs(to_eye.y) / max(d, 0.001), 0.001), slant_max);
	float cover = 1.0 - pow(1.0 - clamp(c.a * COLOR.a, 0.0, 1.0), through);
	ALBEDO = c.rgb;
	ALPHA = cover * smoothstep(0.0, 1.0, share);
	NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
}
