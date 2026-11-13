shader_type spatial;
// A ring on the water surface (WaterRipples.cs): a thin band that spreads out from the centre of a quad lying on the
// surface and fades. Unshaded and unlit, so it reads the same at any time of day.
render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

uniform vec4 tint : source_color = vec4(0.78, 0.80, 0.68, 0.55);
uniform float progress = 0.0;  // 0 when the ring starts, 1 when it has spread out and gone

void fragment() {
	float d = length(UV - vec2(0.5)) * 2.0;                       // 0 at the centre, 1 at the edge of the quad
	float band = 1.0 - abs(d - progress * 0.9) / 0.12;            // a thin band that moves outwards
	ALBEDO = tint.rgb;
	ALPHA = clamp(band, 0.0, 1.0) * tint.a * (1.0 - progress);
}
