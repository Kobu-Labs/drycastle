shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;

// The Unicorn's rainbow trail (UnicornFx / RainbowTrail): a ribbon whose vertex colours are the stripes (red on top to violet) and whose alpha
// is the fade toward the tail. UV.x is metres along the ribbon, UV.y 0 (top) to 1 (bottom) across it. The band is soft at both edges and shimmers along its length.

uniform float brightness : hint_range(0.5, 3.0) = 1.25;

void fragment() {
	float edge = smoothstep(0.0, 0.08, UV.y) * smoothstep(1.0, 0.92, UV.y);
	float shimmer = 0.9 + 0.1 * sin(UV.x * 7.0 - TIME * 5.0);
	ALBEDO = COLOR.rgb * brightness * shimmer;
	ALPHA = COLOR.a * (0.35 + 0.65 * edge);
}
