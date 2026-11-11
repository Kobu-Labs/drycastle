// Foliage tuft (ticket #140), drawn through one MultiMesh per suit. The quad is a vertical card; its tip sways in the
// wind. Colour runs from the suit's base colour at the root to its secondary colour at the tip, with a per-tuft shift.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled;

uniform vec4 base_color : source_color = vec4(0.294, 0.353, 0.196, 1.0);
uniform vec4 secondary_color : source_color = vec4(0.18, 0.227, 0.122, 1.0);
// Tip displacement in metres (camo.json tuft_sway_m maps the 0..255 field onto this).
uniform float sway_amount = 0.03;
// How much each tuft's colour shifts towards the secondary colour, 0..1.
uniform float colour_variation = 0.4;

varying float tip;
varying float variation;

void vertex() {
	// QuadMesh UVs run from the top (v = 0) to the root (v = 1), so the tip is 1 - UV.y
	tip = 1.0 - UV.y;
	vec3 root = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	variation = fract(sin(dot(root.xz, vec2(12.9898, 78.233))) * 43758.5453);
	float phase = root.x * 1.7 + root.z * 2.3;
	VERTEX.x += sin(TIME * 1.8 + phase) * sway_amount * tip * tip;
}

void fragment() {
	float t = clamp(tip * 0.5 + variation * colour_variation, 0.0, 1.0);
	ALBEDO = mix(base_color.rgb, secondary_color.rgb, t);
	ROUGHNESS = 0.95;
}
