shader_type spatial;

// The Unicorn's horn (UnicornFx): a pearl with rainbow speckles. Cells of the horn's own (object) space each hold at most one speck whose hue
// drifts with time and height and which twinkles on its own beat; the pearl itself glints in the colours of the rainbow at a grazing angle.
// Applies the same on the live and the dead horn.

uniform vec3 pearl : source_color = vec3(0.96, 0.93, 0.95);
uniform float speckle_cells = 220.0; // per local unit: the model is baked at x4.7, so the horn is ~0.12 long here
uniform float speckle_share : hint_range(0.0, 1.0) = 0.6;
uniform float glow : hint_range(0.0, 8.0) = 2.5;

varying vec3 lpos;

float hash31(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

vec3 hue(float h) {
	vec3 k = clamp(abs(fract(vec3(h) + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
	return k;
}

void vertex() {
	lpos = VERTEX;
}

void fragment() {
	vec3 p = lpos * speckle_cells;
	vec3 id = floor(p);
	vec3 f = fract(p) - 0.5;
	float h = hash31(id);
	float has = step(1.0 - speckle_share, h);
	float speck = has * smoothstep(0.42, 0.12, length(f));
	float twinkle = 0.5 + 0.5 * sin(TIME * (2.0 + h * 5.0) + h * 40.0);
	vec3 speck_col = hue(fract(h * 7.0 + TIME * 0.2 + lpos.y * 8.0));
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
	vec3 sheen = hue(fract(lpos.y * 8.0 + TIME * 0.1 + fres * 0.6)); // one full rainbow along the horn
	vec3 base = mix(pearl, sheen, 0.85);
	ALBEDO = mix(base, speck_col, speck);
	ROUGHNESS = mix(0.25, 0.1, speck);
	METALLIC = 0.2;
	SPECULAR = 0.8;
	EMISSION = speck_col * speck * (0.25 + 0.75 * twinkle) * glow + sheen * fres * 0.25 + pearl * 0.05;
}
