shader_type spatial;
// Fragments of a shattered glass pane (GlassFx.Shatter, ADR 0007). The mesh is the pane cut into triangles in its own plane (x along the
// width, y up the pane, +z out of it); UV2 of every vertex is its fragment's centre. All fragments are one mesh and one draw: the vertex
// stage throws each fragment out of the pane, spins it and lets it fall, from the uniform t (seconds since the pane broke, tweened by
// the game). Nothing runs per fragment on the CPU.
render_mode blend_mix, cull_disabled, shadows_disabled;

uniform float t = 0.0;
uniform float life = 1.3;            // seconds until the fragments are gone
uniform float speed = 3.5;           // m/s out of the pane
uniform vec2 pane_size = vec2(1.0);  // metres
uniform float seed = 0.0;

vec3 hash3(vec2 p) {
	vec3 q = vec3(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)), dot(p, vec2(419.2, 371.9)));
	return fract(sin(q + seed) * 43758.5453);
}

void vertex() {
	vec3 centre = vec3(UV2, 0.0);
	vec3 h = hash3(UV2);
	// tumble about the fragment's own axis
	vec3 axis = normalize(vec3(h.x - 0.5, h.y - 0.5, (h.z - 0.5) * 0.6) + vec3(0.001));
	float angle = t * (3.0 + 9.0 * h.z);
	vec3 rel = VERTEX - centre;
	rel = rel * cos(angle) + cross(axis, rel) * sin(angle) + axis * dot(axis, rel) * (1.0 - cos(angle));
	// out of the pane, a little away from its centre, each fragment at its own speed
	vec2 outward = UV2 / max(length(pane_size) * 0.5, 0.001);
	vec3 velocity = vec3(outward * speed * 0.6 + (h.xy - 0.5) * speed * 0.5, speed * (0.45 + 0.75 * h.z));
	// gravity in the pane's frame (the frame has no scale, so the transpose is the inverse)
	vec3 gravity = vec3(0.0, -9.8, 0.0) * mat3(MODEL_MATRIX);
	float shrink = 1.0 - smoothstep(0.7, 1.0, t / life);
	VERTEX = centre + rel * shrink + velocity * t + 0.5 * gravity * t * t;
}

void fragment() {
	ALBEDO = vec3(0.74, 0.86, 0.90);
	ROUGHNESS = 0.15;
	SPECULAR = 0.6;
	ALPHA = 0.55 * (1.0 - smoothstep(0.75, 1.0, t / life));
}
