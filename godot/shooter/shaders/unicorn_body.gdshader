shader_type spatial;

// The Unicorn's whole body (UnicornFx): the pack's grey palette keeps its dark details (eyes, nostrils, hooves) and everything light turns into mother of pearl:
// a white coat with soft pastel rainbow bands that drift along the animal, a rainbow rim at a grazing angle and glitter that twinkles. The same on the live and the dead body.
// The colour of the rainbow is left to the mane, the tail and the trail.

uniform sampler2D palette : source_color, filter_nearest;
uniform vec3 pearl : source_color = vec3(0.97, 0.95, 1.0);
uniform float cycles = 2.2;   // rainbows along the body (per local unit: the model is baked at x4.7, a horse is ~0.45 long here)
uniform float speed = 0.12;
uniform float rainbow : hint_range(0.0, 1.0) = 0.16; // share of rainbow in the coat (1: the whole horse in full colour)
uniform float glitter_cells = 260.0;
uniform float glow : hint_range(0.0, 2.0) = 0.35;

varying vec3 lpos;

vec3 hue(float h) {
	return clamp(abs(fract(vec3(h) + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
}

float hash31(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

void vertex() {
	lpos = VERTEX;
}

void fragment() {
	vec3 tex = texture(palette, UV).rgb;
	float lum = dot(tex, vec3(0.299, 0.587, 0.114));
	float mask = smoothstep(0.08, 0.2, lum);
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 2.5);
	vec3 bands = hue(fract(lpos.z * cycles + lpos.y * cycles * 0.6 + TIME * speed + fres * 0.35));
	vec3 coat = mix(pearl * (0.72 + 0.4 * lum), bands, clamp(rainbow + fres * 0.55, 0.0, 1.0));
	vec3 id = floor(lpos * glitter_cells);
	float h = hash31(id);
	float fleck = step(0.9, h) * pow(0.5 + 0.5 * sin(TIME * (2.0 + h * 5.0) + h * 40.0), 4.0);
	vec3 col = mix(tex, coat, mask);
	ALBEDO = col;
	ROUGHNESS = 0.35;
	METALLIC = 0.0;
	SPECULAR = 0.7;
	EMISSION = (col * glow + bands * fres * 0.6 + hue(h * 5.0) * fleck * 1.5) * mask;
}
