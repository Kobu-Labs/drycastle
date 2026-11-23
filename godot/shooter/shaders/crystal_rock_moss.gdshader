shader_type spatial;
// The rock base of a crystal with moss on the faces that look up (RosalineFx): the pack's atlas in a tint, blended to a soft, rough moss green by the face's slope and a blotchy noise.

uniform sampler2D atlas : source_color, filter_linear_mipmap;
uniform bool use_atlas = false;
uniform vec3 tint : source_color = vec3(0.36, 0.42, 0.29);
uniform vec3 moss : source_color = vec3(0.16, 0.38, 0.08);
uniform vec3 moss_light : source_color = vec3(0.42, 0.62, 0.16);
uniform float moss_amount = 1.0;   // 0 = bare rock
uniform float moss_slope = 0.35;   // how far up a face has to look before moss grows on it (normal.y)
uniform float moss_scale = 14.0;
uniform float rough = 0.9;

varying vec3 obj_pos;
varying vec3 obj_n;

float hash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }
float noise(vec3 p) {
	vec3 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i), hash(i + vec3(1, 0, 0)), f.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

void vertex() {
	obj_pos = VERTEX;
	obj_n = NORMAL;
}

void fragment() {
	vec3 rock = tint * (use_atlas ? texture(atlas, UV).rgb : vec3(1.0));
	float n = noise(obj_pos * moss_scale) * 0.6 + noise(obj_pos * moss_scale * 3.1) * 0.4;
	float m = smoothstep(moss_slope - 0.15, moss_slope + 0.2, normalize(obj_n).y + (n - 0.5) * 0.5) * clamp(moss_amount, 0.0, 1.0);
	float fine = noise(obj_pos * moss_scale * 9.0);
	ALBEDO = mix(rock, mix(moss, moss_light, fine * n), m);
	ROUGHNESS = mix(rough, 1.0, m);
}
