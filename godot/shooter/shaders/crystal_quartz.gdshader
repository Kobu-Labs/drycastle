shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_back;

// Quartz-like translucent crystal (ThundernitFx, SmetiskomiteFx). Every crystal of the rock model (a separate island of triangles, numbered in UV2.x by CrystalAssets)
// gets its own tint from tint0..tint7 (cycled by tint_count), so one rock holds crystals of different colours. See-through with a stronger rim, cloudy inclusions
// inside, glassy highlights. glow is a plain, even emission of the tint over the whole crystal (0 = the stone does not glow; ThundernitFx pulses it with the strikes).

uniform vec4 tint0 : source_color = vec4(0.62, 0.3, 0.12, 1.0);
uniform vec4 tint1 : source_color = vec4(0.42, 0.55, 0.16, 1.0);
uniform vec4 tint2 : source_color = vec4(0.72, 0.6, 0.16, 1.0);
uniform vec4 tint3 : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 tint4 : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 tint5 : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 tint6 : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 tint7 : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform int tint_count = 3;
uniform vec4 inclusion_color : source_color = vec4(0.1, 0.07, 0.04, 1.0);
uniform float inclusion : hint_range(0.0, 1.0) = 0.4;
uniform float inclusion_scale = 9.0;
uniform float alpha : hint_range(0.0, 1.0) = 0.55;
uniform float rim_alpha : hint_range(0.0, 1.0) = 0.4;
uniform float rim_power = 2.5;
uniform float glow = 0.0;
uniform float glow_opacity = 0.0; // emission is blended with ALPHA like everything else, so a glowing crystal also gets more opaque or the glow would be washed out
uniform float rough : hint_range(0.0, 1.0) = 0.08;
uniform float frost = 0.0;       // white rime on the faces that look up (FrostbiteFx); 0 = none
uniform float frost_slope = 0.25;

varying vec3 lpos;
varying float isl;
varying vec3 onrm;

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

vec3 pick(int i) {
	if (i == 0) return tint0.rgb;
	if (i == 1) return tint1.rgb;
	if (i == 2) return tint2.rgb;
	if (i == 3) return tint3.rgb;
	if (i == 4) return tint4.rgb;
	if (i == 5) return tint5.rgb;
	if (i == 6) return tint6.rgb;
	return tint7.rgb;
}

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
	onrm = NORMAL;
}

void fragment() {
	int i = int(round(isl)) % max(tint_count, 1);
	vec3 tint = pick(i);
	float cloud = smoothstep(0.45, 0.8, vnoise(lpos * inclusion_scale + isl * 3.7) * 0.65 + vnoise(lpos * inclusion_scale * 2.4) * 0.35);
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), rim_power);
	ALBEDO = mix(tint, inclusion_color.rgb, cloud * inclusion);
	ALPHA = clamp(alpha + fres * rim_alpha + cloud * inclusion * 0.3, 0.0, 1.0);
	ALPHA = clamp(ALPHA + min(glow * glow_opacity, 0.5), 0.0, 1.0);
	EMISSION = tint * glow;
	float rime = smoothstep(frost_slope - 0.2, frost_slope + 0.3, normalize(onrm).y + (vnoise(lpos * 22.0) - 0.5) * 0.7) * frost;
	ALBEDO = mix(ALBEDO, vec3(0.93, 0.97, 1.0), rime);
	ALPHA = clamp(ALPHA + rime * 0.45, 0.0, 1.0);
	ROUGHNESS = mix(rough, 0.85, rime);
	SPECULAR = 1.0;
}
