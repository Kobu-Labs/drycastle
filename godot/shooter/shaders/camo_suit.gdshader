// Camouflage suit (ticket #140). Flat-colour low-poly style: two colours, a pattern of soft or hard blotches in
// model space, so the pattern moves with the body. Pattern ids are indices: keep camo.json "suit_patterns" in this order
// (0 blotch, 1 stripes, 2 leaf, 3 flecktarn); any other id falls back to blotch.
shader_type spatial;
render_mode cull_back, diffuse_lambert, specular_disabled;

uniform vec4 base_color : source_color = vec4(0.294, 0.353, 0.196, 1.0);
uniform vec4 secondary_color : source_color = vec4(0.18, 0.227, 0.122, 1.0);
uniform int pattern = 0;
// Blobs per metre of model space (camo.json pattern_scale_per_m maps the 0..255 field onto this).
uniform float pattern_scale = 1.5;
// 0 gives a flat mix, 1 gives hard edges between the two colours.
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
	float n;
	if (pattern == 1) {
		// stripes: noise stretched along the body's height
		n = value_noise(vec3(p.x * 0.5, p.y * 3.0, p.z * 0.5));
	} else if (pattern == 2) {
		// leaf: two octaves give a broken, leafy edge
		n = 0.65 * value_noise(p * 2.0) + 0.35 * value_noise(p * 5.0);
	} else if (pattern == 3) {
		// flecktarn: hard-edged spots
		n = step(0.55, value_noise(p * 1.4));
	} else {
		// blotch: soft, large
		n = value_noise(p);
	}
	float c = max(contrast, 0.02);
	float t = smoothstep(0.5 - 0.5 * c, 0.5 + 0.5 * c, n);
	ALBEDO = mix(base_color.rgb, secondary_color.rgb, t);
	ROUGHNESS = 0.95;
}
