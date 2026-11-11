shader_type canvas_item;
// Thermal colour grade for the gunship turret view (GunshipOverlay.cs): the picture's brightness is mapped onto a heat
// palette (cold black-violet, then red, orange, yellow and white hot), with a faint scanline. amount 0 draws nothing
// over the picture, 1 is the full grade.
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float amount = 1.0;
uniform float gain = 1.15;     // brightness before the palette lookup
uniform float scanline = 0.035; // depth of the scanline, 0 for none
uniform float mono = 0.0;       // 1 for the AC-130's black-and-white view: white hot, no palette
uniform float auto_expose = 0.0; // 1 to scale the picture so its average brightness sits mid-range (the missile, day and night)

vec3 heat(float t) {
	vec3 cold = vec3(0.02, 0.00, 0.08);
	vec3 violet = vec3(0.35, 0.00, 0.45);
	vec3 red = vec3(0.90, 0.15, 0.08);
	vec3 orange = vec3(1.00, 0.80, 0.10);
	vec3 white = vec3(1.00, 1.00, 0.95);
	if (t < 0.25) return mix(cold, violet, t / 0.25);
	if (t < 0.50) return mix(violet, red, (t - 0.25) / 0.25);
	if (t < 0.75) return mix(red, orange, (t - 0.50) / 0.25);
	return mix(orange, white, (t - 0.75) / 0.25);
}

void fragment() {
	vec3 src = textureLod(screen_tex, SCREEN_UV, 0.0).rgb;
	float exposure = 1.0;
	if (auto_expose > 0.5) {
		// the mip at the top of the chain is the whole picture's average: lift a dark night, pull back a bright day
		vec3 avg = textureLod(screen_tex, vec2(0.5), 12.0).rgb;
		exposure = clamp(0.35 / max(dot(avg, vec3(0.299, 0.587, 0.114)), 0.02), 1.0, 4.0);
	}
	float lum = clamp(dot(src, vec3(0.299, 0.587, 0.114)) * gain * exposure, 0.0, 1.0);
	vec3 graded = mix(heat(lum), vec3(lum), mono);
	graded *= 1.0 - scanline * (0.5 + 0.5 * sin(SCREEN_UV.y * 1400.0));
	COLOR = vec4(mix(src, graded, amount), 1.0);
}
