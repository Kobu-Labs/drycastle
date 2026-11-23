shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;
// Pastel iridescence (KulturiteFx): the hue runs with the angle the face is seen at, the height and the crystal number, washed out towards white; it drifts slowly.

uniform float saturation = 0.45;
uniform float shift = 1.2;         // hue turns per change of viewing angle
uniform float band = 1.5;          // hue turns per metre of height
uniform float drift = 0.03;        // hue turns per second
uniform float alpha = 0.6;
uniform float rim_alpha = 0.35;
uniform float rim_power = 2.5;
uniform float glow = 0.5;
uniform float glow_opacity = 0.15;
uniform float rough = 0.06;

varying vec3 lpos;
varying float isl;

vec3 hue(float h) {
	return clamp(abs(fract(h + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
}

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
}

void fragment() {
	float ndv = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
	float fres = pow(1.0 - ndv, rim_power);
	vec3 col = mix(vec3(1.0), hue((1.0 - ndv) * shift + lpos.y * band + isl * 0.17 + TIME * drift), saturation);
	ALBEDO = col;
	ALPHA = clamp(alpha + fres * rim_alpha + min(glow * glow_opacity, 0.5), 0.0, 1.0);
	EMISSION = col * glow;
	ROUGHNESS = rough;
	METALLIC = 0.2;
	SPECULAR = 1.0;
}
