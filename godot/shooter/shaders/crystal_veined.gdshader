shader_type spatial;
// An opaque crystal with veins (blood, lava cracks, rot), an optional wood grain, specks and one band round one crystal: bloodstone, magmorite, zombite, nazium, dubovite, hovnovite, brother's stone.
// Each crystal of the rock takes one of the tints by its number (UV2.x). Veins are thin lines where two layers of noise cross their middle; they can glow (vein_glow * pulse) and creep (vein_scroll).

uniform vec4 tint0 : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 tint1 : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 tint2 : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 tint3 : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform int tint_count = 1;
uniform vec3 vein_color : source_color = vec3(0.1, 0.0, 0.0);
uniform float vein_amount = 1.0;
uniform float vein_scale = 6.0;
uniform float vein_width = 0.05;
uniform float vein_glow = 0.0;
uniform float vein_scroll = 0.0;
uniform float pulse = 1.0;         // multiplies vein_glow (set every frame by the crystal)
uniform float glow = 0.0;          // even emission of the tint
uniform float mottle = 0.25;
uniform float grain = 0.0;         // wood grain along the crystal (0 = none)
uniform float grain_freq = 60.0;
uniform vec3 speck_color : source_color = vec3(1.0);
uniform float speck = 0.0;         // share of specks (bone chips, embers), 0..1
uniform float speck_scale = 45.0;
uniform float speck_glow = 0.0;
uniform float rough = 0.5;
// one band round one crystal, with a disc on it
uniform int band_island = -1;
uniform float band_y = 0.0;        // model space
uniform float band_width = 0.08;
uniform vec3 band_color : source_color = vec3(0.75, 0.05, 0.05);
uniform vec3 disc_color : source_color = vec3(1.0);
uniform vec3 band_center = vec3(0.0);
uniform float disc_radius = 0.03;
uniform float disc_azimuth = 0.0;

varying vec3 lpos;
varying float isl;

float hash31(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}
float vnoise(vec3 p) {
	vec3 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(mix(hash31(i), hash31(i + vec3(1, 0, 0)), f.x), mix(hash31(i + vec3(0, 1, 0)), hash31(i + vec3(1, 1, 0)), f.x), f.y);
	float b = mix(mix(hash31(i + vec3(0, 0, 1)), hash31(i + vec3(1, 0, 1)), f.x), mix(hash31(i + vec3(0, 1, 1)), hash31(i + vec3(1, 1, 1)), f.x), f.y);
	return mix(a, b, f.z);
}
vec3 pick(int i) {
	if (i == 0) return tint0.rgb;
	if (i == 1) return tint1.rgb;
	if (i == 2) return tint2.rgb;
	return tint3.rgb;
}

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
}

void fragment() {
	int id = int(round(isl));
	vec3 tint = pick(id % max(tint_count, 1));
	vec3 p = lpos * vein_scale + isl * 3.7 + vec3(0.0, TIME * vein_scroll, 0.0);
	float w = vnoise(p) * 0.65 + vnoise(p * 2.1 + 7.0) * 0.35;
	float vein = (1.0 - smoothstep(0.0, vein_width, abs(w - 0.5))) * vein_amount;
	vec3 col = tint * (1.0 - mottle * vnoise(lpos * vein_scale * 0.7 + 3.0));
	float g = sin(lpos.y * grain_freq + vnoise(lpos * vec3(9.0, 2.0, 9.0) + isl) * 7.0);
	col *= 1.0 - grain * 0.4 * smoothstep(-0.2, 0.8, g);
	float sp = step(1.0 - speck * 0.12, hash31(floor(lpos * speck_scale) + isl)) * step(0.001, speck);
	col = mix(col, speck_color, sp);
	col = mix(col, vein_color, vein);
	float by = lpos.y - band_y;
	if (id == band_island && by > 0.0 && by < band_width) {
		vec3 rel = lpos - band_center;
		float ang = atan(rel.z, rel.x) - disc_azimuth;
		ang = atan(sin(ang), cos(ang));
		float d = length(vec2(ang * length(rel.xz), by - band_width * 0.5));
		col = d < disc_radius ? disc_color : band_color;
		vein = 0.0;
		sp = 0.0;
	}
	ALBEDO = col;
	EMISSION = tint * glow + vein_color * vein * vein_glow * pulse + speck_color * sp * speck_glow;
	ROUGHNESS = rough;
	SPECULAR = 0.6;
}
