shader_type canvas_item;
// Blood on the screen (#283, Hud.Blood.cs): a procedural full-screen layer. Dark red thickens from the screen's edges and spots with short drips grow
// from them, always leaving the middle clear for the text and the view. One knob, `intensity` 0..1 (0 draws nothing): the death screen uses it today,
// a low-health vignette can use the same layer with a small value. `seed` picks the pattern, so two deaths do not splatter alike.

uniform float intensity : hint_range(0.0, 1.0) = 0.0;
uniform float seed = 0.0;
uniform vec4 blood_color : source_color = vec4(0.42, 0.0, 0.02, 1.0);
uniform vec4 dark_color : source_color = vec4(0.13, 0.0, 0.01, 1.0);

vec2 hash22(vec2 p) {
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p + seed * 13.7) * 43758.5453);
}

// How far from the middle (0 in the middle, 1 at the corners): where blood may be.
float edge_of(vec2 uv_aspect, float aspect) {
	vec2 q = uv_aspect - vec2(0.5 * aspect, 0.5);
	return smoothstep(0.3, 0.95, length(q) * 1.15);
}

// One layer of blobs on a grid of `scale` cells per screen height. A cell's blob exists with a chance that grows with the edge and the intensity,
// is a little taller than wide, and may run a thin drip down.
float blobs(vec2 uv_aspect, float aspect, float scale, float inten) {
	vec2 g = uv_aspect * scale;
	vec2 id = floor(g);
	vec2 f = fract(g);
	float cover = 0.0;
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			vec2 o = vec2(float(x), float(y));
			vec2 h = hash22(id + o);
			vec2 h2 = hash22(id + o + vec2(19.19, 7.7));
			vec2 centre = o + 0.15 + 0.7 * h;
			float edge = edge_of((id + o + 0.5) / scale, aspect);
			float present = step(h2.x, clamp(edge * inten * 1.3, 0.0, 1.0));
			float r = (0.16 + 0.26 * h2.y) * (0.45 + 0.55 * inten) * (0.6 + 0.4 * edge);
			vec2 d = f - centre;
			vec2 ds = vec2(d.x, d.y * 0.6);
			float blob = 1.0 - smoothstep(r * 0.7, r, length(ds));
			float drip_len = max((0.35 + 0.9 * h.y) * inten, 0.01);
			float drip = (1.0 - smoothstep(0.02, 0.05, abs(d.x))) * step(0.0, d.y) * (1.0 - smoothstep(drip_len * 0.6, drip_len, d.y)) * step(0.45, h2.y);
			cover = max(cover, present * max(blob, drip * 0.9));
		}
	}
	return cover;
}

void fragment() {
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 uva = vec2(UV.x * aspect, UV.y);
	float edge = edge_of(uva, aspect);
	float haze = pow(edge, 2.0) * 0.6 * intensity;
	float spots = max(blobs(uva, aspect, 3.0, intensity), 0.85 * blobs(uva + vec2(5.3, 2.1), aspect, 7.0, intensity));
	float alpha = clamp(haze + spots * 0.92, 0.0, 0.94) * smoothstep(0.0, 0.05, intensity);
	vec3 col = mix(dark_color.rgb, blood_color.rgb, smoothstep(0.1, 1.0, spots));
	COLOR = vec4(col, alpha);
}
