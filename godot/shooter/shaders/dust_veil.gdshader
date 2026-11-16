shader_type canvas_item;
// Dust in the eyes (#345, Hud.Dust.cs): one full-screen layer under the HUD. `intensity` 0..1 is how much of the view is lost (DustField.Veil);
// near 1 the layer is close to solid, so nothing behind it can be made out. The dust is uneven and drifts slowly (`drift` 0 holds it still:
// reduce motion). `dust_color` is set by the time of day.

uniform float intensity : hint_range(0.0, 1.0) = 0.0;
uniform float drift = 1.0;
uniform vec4 dust_color : source_color = vec4(0.62, 0.55, 0.45, 1.0);

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	vec2 p = UV * vec2(3.2, 1.8);
	float t = TIME * drift;
	float n = noise(p * 1.5 + vec2(t * 0.11, t * 0.04)) * 0.6 + noise(p * 4.0 - vec2(t * 0.07, t * 0.13)) * 0.4;
	// thin dust shows its unevenness, thick dust closes up
	float a = clamp(intensity * (1.0 + (n - 0.5) * 0.9 * (1.0 - intensity)), 0.0, 1.0);
	COLOR = vec4(dust_color.rgb * (0.85 + 0.3 * n), a);
}
