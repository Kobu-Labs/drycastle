shader_type canvas_item;
// Full-screen underwater look (UnderwaterView.cs): the screen is blurred a little, tinted toward the water colour,
// hazed (a fog stand-in: the sky and far field go to the tint), and darkened at the edges. amount 0 is not drawn at all.
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float amount = 0.0;          // 0 above the surface, 1 fully under (UnderwaterFx.Amount)
uniform vec3 tint : source_color = vec3(0.16, 0.40, 0.46);
uniform float blur_lod = 1.6;        // mip level at full amount: a soft blur without a multi-pass filter
uniform float haze = 0.45;           // how far the tint replaces the picture at full amount
uniform float vignette = 0.5;        // edge darkening at full amount

void fragment() {
	vec2 uv = SCREEN_UV;
	vec3 col = textureLod(screen_tex, uv, blur_lod * amount).rgb;
	col = mix(col, col * tint * 1.25, 0.6 * amount);
	col = mix(col, tint * 0.6, haze * amount);
	vec2 d = (uv - 0.5) * vec2(1.2, 1.0);
	float v = smoothstep(0.25, 0.85, length(d));
	col *= 1.0 - v * vignette * amount;
	COLOR = vec4(col, 1.0);
}
