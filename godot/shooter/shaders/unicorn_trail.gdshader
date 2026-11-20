shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never;

// The Unicorn's rainbow trail (UnicornFx / RainbowTrail): a tube round the path it went. UV.x is metres along it, UV.y 0 (top) to 1 (bottom): the seven stripes are cut
// here from UV.y (red on top to violet). The vertex alpha is the fade toward the tail. The tube reads as a body with volume: bright in the middle, soft and thin at its
// outline, a gloss along its upper side; bulges run down it and glints twinkle on it.

uniform float brightness : hint_range(0.5, 3.0) = 1.3;
uniform float violet = 0.78;   // hue of the bottom stripe (RainbowTrail.VioletHue)
uniform float stripes = 7.0;

vec3 hue(float h) {
	return clamp(abs(fract(vec3(h) + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
}

float hash21(vec2 p) {
	p = fract(p * vec2(0.1031, 0.1030));
	p += dot(p, p.yx + 33.33);
	return fract((p.x + p.y) * p.x);
}

void vertex() {
	// bulges that travel down the tube, and a slow roll of the whole thing (none at the live end, where the alpha-free taper hides it anyway)
	float bulge = 0.5 + 0.5 * sin(UV.x * 2.4 - TIME * 4.0);
	VERTEX += NORMAL * (0.07 * bulge);
	VERTEX.y += 0.06 * sin(UV.x * 0.9 - TIME * 2.2);
}

void fragment() {
	float s = clamp(UV.y, 0.0, 0.9999) * stripes;
	float band = floor(s);
	float f = fract(s);
	// stripes with a soft seam between them
	float h0 = band / (stripes - 1.0) * violet;
	float h1 = min(band + 1.0, stripes - 1.0) / (stripes - 1.0) * violet;
	vec3 col = mix(hue(h0), hue(h1), smoothstep(0.78, 1.0, f));
	col = mix(col, vec3(1.0), 0.1);
	float facing = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
	float gloss = pow(facing, 5.0) * smoothstep(0.75, 0.15, UV.y);
	vec2 cell = floor(vec2(UV.x * 9.0, UV.y * 14.0));
	float h = hash21(cell);
	float glint = step(0.93, h) * pow(0.5 + 0.5 * sin(TIME * (3.0 + h * 6.0) + h * 50.0), 6.0);
	float shimmer = 0.9 + 0.1 * sin(UV.x * 7.0 - TIME * 5.0);
	ALBEDO = col * brightness * shimmer * (0.7 + 0.45 * facing) + vec3(1.0) * (gloss * 0.3 + glint * 0.9);
	ALPHA = COLOR.a * (0.2 + 0.8 * smoothstep(0.0, 0.55, facing));
}
