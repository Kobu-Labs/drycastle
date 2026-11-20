shader_type spatial;
render_mode cull_disabled;

// The Unicorn's mane, forelock and tail (UnicornFx): locks of hair built on the neck, head and tail bones. COLOR is the lock's colour, UV.x its own phase (0..1),
// UV.y 0 at the root to 1 at the tip. The tips sway (in world space, so every lock moves the same way whatever its bone) and stream out along `drift`
// (metres, world: against the way the animal moves).

uniform vec3 drift = vec3(0.0);
uniform float sway = 0.07;
uniform float glow : hint_range(0.0, 2.0) = 0.45;

void vertex() {
	float w = UV.y * UV.y;
	float ph = UV.x * 6.2832;
	vec3 d = vec3(sin(TIME * 2.3 + ph + UV.y * 3.0), 0.35 * sin(TIME * 1.7 + ph * 1.3), cos(TIME * 1.9 + ph * 0.7 + UV.y * 2.0)) * sway + drift;
	// world offset to the mesh's own space: the model matrix is a rotation and one scale
	VERTEX += (vec4(d * w, 0.0) * MODEL_MATRIX).xyz / dot(MODEL_MATRIX[0].xyz, MODEL_MATRIX[0].xyz);
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 0.45;
	SPECULAR = 0.6;
	EMISSION = COLOR.rgb * glow;
}
