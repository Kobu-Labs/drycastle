shader_type spatial;

// The Unicorn's whole body (UnicornFx): the pack's grey palette keeps its dark details (eyes, nostrils, hooves, shadows of the mane) and everything light
// turns into a rainbow that runs along the animal and drifts with time. The same on the live and the dead body.

uniform sampler2D palette : source_color, filter_nearest;
uniform float cycles = 2.2;   // rainbows along the body (per local unit: the model is baked at x4.7, a horse is ~0.45 long here)
uniform float speed = 0.12;
uniform float glow : hint_range(0.0, 2.0) = 0.35;

varying vec3 lpos;

vec3 hue(float h) {
	vec3 k = clamp(abs(fract(vec3(h) + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
	return k;
}

void vertex() {
	lpos = VERTEX;
}

void fragment() {
	vec3 tex = texture(palette, UV).rgb;
	float lum = dot(tex, vec3(0.299, 0.587, 0.114));
	float mask = smoothstep(0.08, 0.2, lum);
	vec3 rainbow = hue(fract(lpos.z * cycles + lpos.y * cycles * 0.6 + TIME * speed));
	vec3 col = mix(tex, rainbow * (0.55 + 0.6 * lum), mask);
	ALBEDO = col;
	ROUGHNESS = 0.55;
	METALLIC = 0.0;
	EMISSION = col * glow * mask;
}
