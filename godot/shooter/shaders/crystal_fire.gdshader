shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back;

// Pheonixite (PheonixiteFx): a thin skin of dark glass around fire that burns INSIDE the crystal. The fire is a scrolling 3D noise of the crystal's own
// (object) space, sampled at three depths along the view ray so it has volume and shifts as the camera moves. Hot near the base, cooling to the tip.
// All tunables arrive from data/crystals.json through PheonixiteFx.

uniform vec4 core_color : source_color = vec4(1.0, 0.12, 0.0, 1.0);
uniform vec4 mid_color : source_color = vec4(1.0, 0.45, 0.04, 1.0);
uniform vec4 tip_color : source_color = vec4(1.0, 0.85, 0.35, 1.0);
uniform vec4 glass_tint : source_color = vec4(0.12, 0.02, 0.01, 1.0);
uniform float speed = 0.9;
uniform float noise_scale = 7.0;
uniform float energy = 3.0;
uniform float glass_alpha : hint_range(0.0, 1.0) = 0.55;
uniform float rim_power = 2.5;
uniform float rim_energy = 0.8;
uniform float depth_step = 0.035;
uniform float y_min = 0.0;
uniform float y_max = 1.0;
uniform float cooling = 0.55;

varying vec3 lpos;
varying vec3 lview; // object-space direction from the surface to the camera

float hash31(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(mix(hash31(i), hash31(i + vec3(1, 0, 0)), f.x), mix(hash31(i + vec3(0, 1, 0)), hash31(i + vec3(1, 1, 0)), f.x), f.y);
	float b = mix(mix(hash31(i + vec3(0, 0, 1)), hash31(i + vec3(1, 0, 1)), f.x), mix(hash31(i + vec3(0, 1, 1)), hash31(i + vec3(1, 1, 1)), f.x), f.y);
	return mix(a, b, f.z);
}

float fbm(vec3 p) {
	return vnoise(p) * 0.55 + vnoise(p * 2.07 + 11.3) * 0.3 + vnoise(p * 4.1 + 3.7) * 0.15;
}

float flame(vec3 p) {
	float h = clamp((p.y - y_min) / max(y_max - y_min, 0.001), 0.0, 1.0);
	vec3 q = p * noise_scale;
	q.y += TIME * speed * 2.0; // the pattern slides down, so the fire reads as rising
	q.x += sin(TIME * 0.7 + p.y * 5.0) * 0.25;
	float n = fbm(q);
	return clamp((n - h * cooling + 0.3) * 1.7, 0.0, 1.0);
}

void vertex() {
	lpos = VERTEX;
	vec3 vpos = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	lview = normalize((inverse(MODELVIEW_MATRIX) * vec4(-vpos, 0.0)).xyz);
}

void fragment() {
	float f = 0.0;
	for (int i = 0; i < 3; i++) {
		f = max(f, flame(lpos - lview * depth_step * float(i)) * (1.0 - 0.18 * float(i)));
	}
	vec3 col = mix(core_color.rgb, mid_color.rgb, smoothstep(0.2, 0.55, f));
	col = mix(col, tip_color.rgb, smoothstep(0.6, 1.0, f));
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), rim_power);
	ALBEDO = glass_tint.rgb;
	EMISSION = col * f * energy + mid_color.rgb * fres * rim_energy;
	ROUGHNESS = 0.04;
	SPECULAR = 1.0;
	ALPHA = clamp(mix(glass_alpha, 1.0, f) + fres * 0.3, 0.0, 1.0);
}
