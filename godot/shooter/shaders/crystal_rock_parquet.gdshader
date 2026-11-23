shader_type spatial;
// The rock base as a parquet floor (KulturiteFx): squares of four oak planks, every other square turned a quarter (the culture house's "parkety"), dark seams, each plank its own shade with a faint grain.
// Laid on the plane each face looks along most, so the top is a floor and the sides are panelled.

uniform vec3 wood : source_color = vec3(0.62, 0.4, 0.2);
uniform vec3 wood_dark : source_color = vec3(0.4, 0.24, 0.11);
uniform vec3 seam_color : source_color = vec3(0.12, 0.07, 0.04);
uniform float square = 0.09;       // side of one square of planks, metres
uniform float planks = 4.0;
uniform float seam = 0.035;        // seam width as a share of a plank
uniform float rough = 0.35;        // varnished

varying vec3 obj_pos;
varying vec3 obj_n;

float hash(vec3 p) { return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453); }

void vertex() {
	obj_pos = VERTEX;
	obj_n = NORMAL;
}

void fragment() {
	vec3 a = abs(normalize(obj_n));
	vec2 uv = a.y >= a.x && a.y >= a.z ? obj_pos.xz : (a.x > a.z ? obj_pos.zy : obj_pos.xy);
	vec2 g = uv / square;
	vec2 cell = floor(g);
	vec2 f = fract(g);
	if (mod(cell.x + cell.y, 2.0) > 0.5) f = f.yx;
	float plank = floor(f.x * planks);
	float px = fract(f.x * planks);
	float edge = min(min(px, 1.0 - px), min(f.y, 1.0 - f.y) * planks);
	float shade = hash(vec3(cell, plank));
	float grain = hash(vec3(floor(f.x * planks * 14.0), cell)) * 0.12;
	vec3 col = mix(wood_dark, wood, shade) * (0.92 + grain);
	ALBEDO = mix(seam_color, col, smoothstep(seam * 0.5, seam, edge));
	ROUGHNESS = rough;
	SPECULAR = 0.6;
}
