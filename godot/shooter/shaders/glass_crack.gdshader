shader_type spatial;
// Crack overlay of a glass pane (GlassFx.cs, ADR 0007): a quad just in front of the pane. While the pane stands, cracks spread from an
// impact point and reach further with each crack stage; a shattered pane shows a dark opening with a few shards left along its edge.
// The crack pattern is the borders of a Voronoi diagram in the pane's own metres, so every pane has its own and it does not change
// between stages (the crack grows, it is not redrawn). The quad is the pane's bounding rectangle; a pane with an outline (a triangle, a
// trapezoid: convex, counter-clockwise, in metres from the quad's centre, u right, v up) is clipped to it, and the shards left along the edge
// of a shattered pane follow it.
render_mode blend_mix, cull_disabled, shadows_disabled, specular_disabled, depth_draw_never;

uniform float stage = 1.0;          // crack stage, 1..stages
uniform float stages = 3.0;         // the highest crack stage
uniform float shattered = 0.0;      // 1: the pane is gone
uniform float hole_darkness = 0.9;  // opacity of the dark opening of a shattered pane
uniform vec2 pane_size = vec2(1.0); // metres
uniform float seed = 0.0;
uniform float cells_per_m = 5.0;
uniform vec2 outline[12];           // the pane's outline polygon (see GlassFx.SetOutline)
uniform int outline_count = 0;      // 0: the pane is the whole rectangle

vec2 hash2(vec2 p) {
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p + seed) * 43758.5453);
}

// Metres from p (from the pane's centre) to the nearest edge of the pane, positive inside it.
float pane_edge_distance(vec2 p) {
	if (outline_count < 3) {
		return min(pane_size.x * 0.5 - abs(p.x), pane_size.y * 0.5 - abs(p.y));
	}
	float d = 1.0e6;
	for (int i = 0; i < 12; i++) {
		if (i >= outline_count) { break; }
		vec2 a = outline[i];
		vec2 e = outline[(i + 1) % outline_count] - a;
		d = min(d, (e.x * (p.y - a.y) - e.y * (p.x - a.x)) / max(length(e), 0.00001));
	}
	return d;
}

// Distance to the nearest cell border (in cells), and the vector to the cell's own point.
float cell_border(vec2 x, out vec2 to_point) {
	vec2 n = floor(x);
	vec2 f = fract(x);
	vec2 mg = vec2(0.0);
	vec2 mr = vec2(0.0);
	float md = 8.0;
	for (int j = -1; j <= 1; j++) {
		for (int i = -1; i <= 1; i++) {
			vec2 g = vec2(float(i), float(j));
			vec2 r = g + hash2(n + g) - f;
			float d = dot(r, r);
			if (d < md) { md = d; mr = r; mg = g; }
		}
	}
	md = 8.0;
	for (int j = -2; j <= 2; j++) {
		for (int i = -2; i <= 2; i++) {
			vec2 g = mg + vec2(float(i), float(j));
			vec2 r = g + hash2(n + g) - f;
			if (dot(mr - r, mr - r) > 0.00001) md = min(md, dot(0.5 * (mr + r), normalize(r - mr)));
		}
	}
	to_point = mr;
	return md;
}

void fragment() {
	vec2 p = vec2(UV.x - 0.5, 0.5 - UV.y) * pane_size; // metres from the pane's centre, u right, v up
	if (pane_edge_distance(p) < 0.0) { discard; }     // outside the pane's outline
	vec2 to_point;
	float border = cell_border(p * cells_per_m, to_point);
	float crack_line = 1.0 - smoothstep(0.0, 0.05, border);

	// the impact point: somewhere in the middle half of the pane, fixed per pane
	vec2 impact = (hash2(vec2(3.7, 9.1)) - 0.5) * pane_size * 0.5;
	float reach = length(pane_size) * clamp(stage / max(stages, 1.0), 0.0, 1.0) * 0.75;
	float dist = length(p - impact);
	float cracked = 1.0 - smoothstep(reach * 0.75, reach, dist);
	float bruise = 1.0 - smoothstep(0.0, 0.06, dist);

	vec3 glass_white = vec3(0.90, 0.95, 1.0);
	vec3 col = glass_white;
	float alpha = max(crack_line * cracked * 0.85, bruise * 0.9) + cracked * 0.06;

	if (shattered > 0.5) {
		// cells whose point lies near the pane's edge stay as shards; the rest is the opening
		vec2 c = p + to_point / cells_per_m;
		float to_edge = pane_edge_distance(c);
		float keep = 0.05 + 0.10 * hash2(floor(c * 40.0)).x;
		if (to_edge < keep) {
			alpha = crack_line * 0.85 + 0.10;
		} else {
			col = vec3(0.025, 0.03, 0.035);
			alpha = hole_darkness;
		}
	}
	ALBEDO = col;
	ALPHA = clamp(alpha, 0.0, 1.0);
}
