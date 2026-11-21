shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;
// A river behind a thin skin of glass (PovodnoviteFx): blue-green water scrolling through the crystal, thin white wave crests where two layers of noise meet, a glassy rim.

uniform vec3 deep : source_color = vec3(0.02, 0.22, 0.3);
uniform vec3 shallow : source_color = vec3(0.2, 0.7, 0.62);
uniform vec3 foam : source_color = vec3(0.92, 1.0, 1.0);
uniform float flow_speed = 0.25;
uniform float scale = 7.0;
uniform float stretch = 0.4;       // < 1 draws the water out along the flow (x)
uniform float crest = 0.6;
uniform float crest_width = 0.06;
uniform float alpha = 0.72;
uniform float rim_alpha = 0.3;
uniform float rim_power = 2.5;
uniform float glow = 0.25;
uniform float bubbles = 0.0;      // bubbles rising through the liquid (VenomiteFx); 0 = none
uniform float bubble_scale = 26.0;
uniform float bubble_speed = 0.12;
uniform float turbulence = 0.6;    // how hard the flow is churned (0 = a calm stream)
uniform float surge = 0.25;        // slow swells of white water passing through

varying vec3 lpos;
varying float isl;

float hash31(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}
float vnoise(vec3 p) {
	vec3 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(mix(hash31(i), hash31(i + vec3(1, 0, 0)), f.x), mix(hash31(i + vec3(0, 1, 0)), hash31(i + vec3(1, 1, 0)), f.x), f.y);
	float b = mix(mix(hash31(i + vec3(0, 0, 1)), hash31(i + vec3(1, 0, 1)), f.x), mix(hash31(i + vec3(0, 1, 1)), hash31(i + vec3(1, 1, 1)), f.x), f.y);
	return mix(a, b, f.z);
}

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
}

void fragment() {
	vec3 p = lpos * scale * vec3(stretch, 1.0, 1.0) + isl * 2.3;
	float t = TIME * flow_speed;
	vec3 churn = vec3(vnoise(p * 0.8 + vec3(0.0, t * 1.7, 3.1)), vnoise(p * 0.8 + vec3(5.2, -t * 1.3, 0.0)), vnoise(p * 0.8 + vec3(t * 1.1, 2.7, 9.4))) - 0.5;
	p += churn * turbulence * 2.5;
	float w = vnoise(p + vec3(-t * 2.0, t * 0.3, 0.0)) * 0.6 + vnoise(p * 2.3 + vec3(-t * 3.1, 0.0, t)) * 0.4;
	float w2 = vnoise(p * 1.6 + vec3(-t * 2.6, -t * 0.8, 4.0));
	float line = 1.0 - smoothstep(0.0, crest_width, abs(w - 0.5));
	line = max(line, (1.0 - smoothstep(0.0, crest_width * 1.4, abs(w2 - 0.5))) * turbulence);
	float swell = smoothstep(0.55, 0.9, vnoise(vec3(lpos.x * 2.0 - TIME * flow_speed * 3.0, lpos.y * 1.5, TIME * 0.4)));
	line = clamp(line + swell * surge * (0.5 + w2), 0.0, 1.0);
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), rim_power);
	vec3 water = mix(deep, shallow, smoothstep(0.25, 0.75, w));
	vec3 bp = lpos * bubble_scale;
	bp.y -= TIME * bubble_speed * bubble_scale;
	vec3 bc = floor(bp);
	vec3 off = (vec3(hash31(bc), hash31(bc + 5.1), hash31(bc + 9.7)) - 0.5) * 0.5;
	float bd = length(fract(bp) - 0.5 - off);
	float br = 0.1 + 0.14 * hash31(bc + 2.3);
	float bub = step(0.55, hash31(bc + 7.7)) * (smoothstep(br, br * 0.75, bd) - 0.6 * smoothstep(br * 0.6, br * 0.3, bd)) * bubbles;
	line = clamp(line + bub / max(crest, 0.05), 0.0, 1.0);
	ALBEDO = mix(water, foam, line * crest);
	ALPHA = clamp(alpha + fres * rim_alpha + line * crest * 0.25, 0.0, 1.0);
	EMISSION = water * glow + foam * line * crest * glow;
	ROUGHNESS = 0.03;
	SPECULAR = 1.0;
}
