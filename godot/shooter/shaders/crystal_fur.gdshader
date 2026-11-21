shader_type spatial;
// Fur or straw on a crystal (MicinFx: white fur; SeninFx: a hay bale, coarser golden strands with burnt patches): fine strands running down each face (noise drawn out along the height), tufts of lighter and darker coat, and a soft fuzzy edge where a face turns away from the
// eye (velvet rim) so the hard crystal reads as a coat. A light even glow.

uniform vec3 fur : source_color = vec3(0.96, 0.95, 0.92);
uniform vec3 fur_shadow : source_color = vec3(0.74, 0.72, 0.68);
uniform float strand_scale = 160.0;  // strands per metre across
uniform float strand_length = 0.06;  // how far a strand is drawn out along the height (smaller = longer)
uniform float strand_depth = 0.55;   // how dark the gaps between strands get
uniform float tuft_scale = 9.0;
uniform float fuzz = 0.7;            // lightening of the edges
uniform float fuzz_power = 2.0;
uniform float glow = 0.25;
uniform float scorch = 0.0;          // burnt black patches (SeninFx); 0 = none
uniform float scorch_scale = 6.0;

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

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
}

void fragment() {
	float tuft = vnoise(lpos * tuft_scale + isl * 5.0);
	// strands: fine across, long along the height, combed a little sideways by the tufts
	vec3 sp = vec3(lpos.x * strand_scale, lpos.y * strand_scale * strand_length + tuft * 3.0, lpos.z * strand_scale);
	float strand = vnoise(sp) * 0.6 + vnoise(sp * vec3(2.3, 1.0, 2.3) + 11.0) * 0.4;
	float coat = mix(1.0 - strand_depth, 1.0, smoothstep(0.25, 0.75, strand));
	float rim = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), fuzz_power);
	vec3 col = mix(fur_shadow, fur, coat * (0.75 + 0.25 * tuft));
	col = mix(col, vec3(1.0), rim * fuzz);
	float burnt = scorch * smoothstep(0.55, 0.8, vnoise(lpos * scorch_scale + isl * 2.0 + 20.0));
	col = mix(col, vec3(0.05, 0.04, 0.03), burnt);
	ALBEDO = col;
	EMISSION = fur * glow * (0.6 + 0.4 * coat) * (1.0 - burnt);
	ROUGHNESS = 1.0;
	SPECULAR = 0.1;
}
