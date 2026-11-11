// Camouflage suit (ticket #140). Flat-colour low-poly style: two colours laid out by a pattern in model space, so the
// pattern moves with the body. Pattern ids are indices: keep camo.json "suit_patterns" in this order
// (0 blotch, 1 stripes, 2 leaf, 3 flecktarn); any other id falls back to blotch.
shader_type spatial;
render_mode cull_back, diffuse_lambert, specular_disabled;

uniform vec4 base_color : source_color = vec4(0.294, 0.353, 0.196, 1.0);
uniform vec4 secondary_color : source_color = vec4(0.18, 0.227, 0.122, 1.0);
uniform int pattern = 0;
// Pattern cells per unit of model space (camo.json pattern_scale_per_m maps the 0..255 field onto this).
uniform float pattern_scale = 4.0;
// How far apart the two colours are drawn: 0 both are their average (a plain suit), 0.5 as picked, 1 pushed apart to twice the difference.
uniform float contrast = 0.5;

varying vec3 model_pos;

// Cheap 3D hash in 0..1 (Dave Hoskins style, no sin, so it is stable across GPUs).
float hash13(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

// Trilinear value noise in 0..1.
float value_noise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	float c000 = hash13(i);
	float c100 = hash13(i + vec3(1.0, 0.0, 0.0));
	float c010 = hash13(i + vec3(0.0, 1.0, 0.0));
	float c110 = hash13(i + vec3(1.0, 1.0, 0.0));
	float c001 = hash13(i + vec3(0.0, 0.0, 1.0));
	float c101 = hash13(i + vec3(1.0, 0.0, 1.0));
	float c011 = hash13(i + vec3(0.0, 1.0, 1.0));
	float c111 = hash13(i + vec3(1.0, 1.0, 1.0));
	float x00 = mix(c000, c100, u.x);
	float x10 = mix(c010, c110, u.x);
	float x01 = mix(c001, c101, u.x);
	float x11 = mix(c011, c111, u.x);
	return mix(mix(x00, x10, u.y), mix(x01, x11, u.y), u.z);
}

void vertex() {
	model_pos = VERTEX;
}

void fragment() {
	vec3 p = model_pos * pattern_scale;
	// t: 0 draws the base colour, 1 the secondary. Each pattern has its own shape and its own edge.
	float t;
	if (pattern == 1) {
		// stripes: long horizontal bands with a wavy edge
		float warp = value_noise(p * 0.7) * 1.6;
		float n = value_noise(vec3(p.x * 0.3, p.y * 1.6 + warp, p.z * 0.3));
		t = smoothstep(0.47, 0.53, n);
	} else if (pattern == 2) {
		// leaf: two octaves give a broken, leafy edge
		float n = 0.6 * value_noise(p) + 0.4 * value_noise(p * 2.7 + 11.0);
		t = smoothstep(0.46, 0.54, n);
	} else if (pattern == 3) {
		// flecktarn: hard spots in two sizes
		float spots_wide = step(0.56, value_noise(p * 0.8));
		float spots_fine = step(0.68, value_noise(p * 2.6 + 5.0));
		t = max(spots_wide, spots_fine);
	} else {
		// blotch: soft, large
		t = smoothstep(0.3, 0.7, value_noise(p * 0.6));
	}
	vec3 mid = 0.5 * (base_color.rgb + secondary_color.rgb);
	float spread = contrast * 2.0;
	vec3 a = clamp(mid + (base_color.rgb - mid) * spread, 0.0, 1.0);
	vec3 b = clamp(mid + (secondary_color.rgb - mid) * spread, 0.0, 1.0);
	ALBEDO = mix(a, b, t);
	ROUGHNESS = 0.95;
}
