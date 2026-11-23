shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled, shadows_disabled, world_vertex_coords;

// The Phoenix's glow (PhoenixFx): a streak of flame along one axis of the bird, turned about that axis to face the camera, so the glow has the bird's
// direction (a tail of fire behind it, a bar of fire along its wings) and is not a round blob. Drawn on a unit quad: UV.y runs along the axis from
// `start` over `len` metres, UV.x across `width`. Additive and HDR (it blooms, ADR 0030), no fog (it is a light in the night sky).

uniform vec3 axis = vec3(0.0, 0.0, 1.0); // local, unit: where the streak runs
uniform float start = 0.0;               // metres along the axis where it begins
uniform float len = 30.0;
uniform float width = 5.0;
uniform float sym = 0.0;                 // 0: brightest at its start, dying to its end (the tail). 1: brightest in the middle, dying to both ends (the wings)
uniform vec3 hot = vec3(6.0, 3.2, 1.0);  // the core of the flame (HDR)
uniform vec3 cool = vec3(3.0, 0.6, 0.08); // its edge and its end
uniform float power = 1.0;
uniform float motion = 1.0;              // 0: a still flame (reduced motion)

void vertex() {
	vec3 o = MODEL_MATRIX[3].xyz;
	vec3 a = normalize(mat3(MODEL_MATRIX) * axis);
	vec3 p = o + a * (start + UV.y * len);
	vec3 s = cross(a, CAMERA_POSITION_WORLD - p);
	float sl = length(s);
	s = sl > 0.0001 ? s / sl : vec3(0.0, 1.0, 0.0); // seen end on: any side will do, the streak is a dot then
	VERTEX = p + s * (UV.x - 0.5) * width;
}

void fragment() {
	float t = mix(UV.y, abs(UV.y * 2.0 - 1.0), sym); // 0 where it is brightest, 1 where it ends
	float x = abs(UV.x * 2.0 - 1.0);
	float m = UV.y * len;
	// tongues of flame: the edge wavers along the length and the waves run to the end
	float lick = 0.5 * sin(m * 0.9 - TIME * 9.0 * motion) + 0.5 * sin(m * 0.37 - TIME * 5.3 * motion + 1.7);
	float half_w = mix(1.0, 0.18, t) * (0.82 + 0.18 * lick);
	float across = 1.0 - smoothstep(0.0, half_w, x);
	across *= across;
	float along = pow(1.0 - t, 1.5) * mix(smoothstep(0.0, 0.03, UV.y), 1.0, sym);
	ALBEDO = mix(cool, hot, across * (1.0 - t)) * power;
	ALPHA = clamp(across * along, 0.0, 1.0);
}
