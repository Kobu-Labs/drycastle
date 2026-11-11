shader_type spatial;
// Flat-colour water surface (WaterSurfaces.cs). Two-sided: the top is the lake colour, the underside is tinted darker.
// The shore fades the surface out where it meets the bed, from the scene depth (hint_depth_texture, Forward+).
render_mode blend_mix, cull_disabled, specular_schlick_ggx;

uniform vec4 surface_colour : source_color = vec4(0.20, 0.42, 0.52, 0.55);
uniform vec4 underside_colour : source_color = vec4(0.05, 0.14, 0.18, 0.80);
uniform float ripple_amp = 0.05;   // metres; set to 0 with reduce motion (WaterSurfaces.ApplyMotion)
uniform float ripple_speed = 0.8;
uniform float shore_fade_m = 1.2;  // metres of scene depth over which the edge fades out
uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;

void vertex() {
	// slow, cheap ripple: two travelling sines on the flat surface; the normal is their analytic gradient
	float kx = 0.35;
	float kz = 0.27;
	float px = VERTEX.x * kx + TIME * ripple_speed;
	float pz = VERTEX.z * kz - TIME * ripple_speed * 0.7;
	VERTEX.y += (sin(px) + sin(pz)) * ripple_amp;
	NORMAL = normalize(vec3(-cos(px) * kx * ripple_amp, 1.0, -cos(pz) * kz * ripple_amp));
}

void fragment() {
	// scene depth behind this pixel, in view space, to measure how far the water is in front of the bed
	float raw_depth = texture(DEPTH_TEXTURE, SCREEN_UV).r;
	vec4 scene = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	scene.xyz /= scene.w;
	float water_dist = -VERTEX.z;
	float shore = clamp((-scene.z - water_dist) / shore_fade_m, 0.0, 1.0);

	if (FRONT_FACING) {
		ALBEDO = surface_colour.rgb;
		ALPHA = surface_colour.a * shore;
		ROUGHNESS = 0.12;
		SPECULAR = 0.6;
	} else {
		ALBEDO = underside_colour.rgb;
		ALPHA = underside_colour.a * shore;
		ROUGHNESS = 0.4;
		SPECULAR = 0.2;
	}
	METALLIC = 0.0;
}
