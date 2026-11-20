shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

// One sparkle of the Unicorn's horn (UnicornFx): a four-pointed star on a soft core, a billboard. The colour and fade come from the particle (COLOR),
// its size from the particle's scale; each twinkles by its position.

varying float seed;

void vertex() {
	seed = MODEL_MATRIX[3].x * 13.0 + MODEL_MATRIX[3].z * 7.0;
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0));
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	vec2 a = abs(p);
	float star = exp(-a.x * 22.0) * exp(-a.y * 3.0) + exp(-a.y * 22.0) * exp(-a.x * 3.0);
	float core = exp(-length(p) * 7.0);
	float tw = 0.7 + 0.3 * sin(TIME * 9.0 + seed);
	float k = clamp(star + core, 0.0, 1.5) * tw;
	ALBEDO = mix(COLOR.rgb, vec3(1.0), 0.35) * k * 2.0;
	ALPHA = clamp(k, 0.0, 1.0) * COLOR.a;
}
