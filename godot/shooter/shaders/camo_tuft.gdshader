// Foliage tuft (ticket #140), drawn through one MultiMesh per bone. Each instance is one card standing on its root
// (the quad's bottom edge is at the instance origin); the alpha mask cuts the card to the vegetation look
// (camo.json "vegetation" order: 0 grass blade, 1 fern frond, 2 bush leaf). The tip sways in the wind. Each card takes
// a colour between the suit's two colours, darker at the root so the foliage reads against the suit under it.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled;

uniform vec4 base_color : source_color = vec4(0.294, 0.353, 0.196, 1.0);
uniform vec4 secondary_color : source_color = vec4(0.18, 0.227, 0.122, 1.0);
// Tip displacement in model units (camo.json tuft_sway_m maps the 0..255 field onto this).
uniform float sway_amount = 0.03;
// How much the cards differ from each other in colour, 0..1: 0 all take the base colour, 1 they spread over both suit colours and vary in brightness.
uniform float colour_variation = 0.4;
// The vegetation look: 0 grass, 1 fern, 2 bush.
uniform int shape = 0;

varying float tip;
varying float variation;
varying float shade;

void vertex() {
	// QuadMesh UVs run from the top (v = 0) to the root (v = 1), so the tip is 1 - UV.y
	tip = 1.0 - UV.y;
	// per-card values come from the instance index, so they stay fixed while the player moves, turns or animates
	float id = float(INSTANCE_ID);
	variation = fract(sin(id * 12.9898) * 43758.5453);
	shade = fract(sin(id * 78.233) * 12543.1337);
	float phase = id * 1.7;
	VERTEX.x += sin(TIME * 1.8 + phase) * sway_amount * tip * tip;
}

void fragment() {
	float x = abs(UV.x - 0.5) * 2.0; // 0 on the midrib, 1 at the card's edge
	float width;
	if (shape == 1) {
		// fern: a frond that widens above the root, narrows to the tip, with a toothed edge
		float outline = sin(clamp(tip, 0.0, 1.0) * 3.14159) * 0.75 + 0.2 * (1.0 - tip);
		float teeth = 0.55 + 0.45 * abs(sin(tip * 28.0));
		width = outline * teeth;
	} else if (shape == 2) {
		// bush: a broad round leaf on a short stalk
		float r = (tip - 0.6) / 0.4;
		width = tip < 0.2 ? 0.12 : sqrt(max(0.0, 1.0 - r * r));
	} else {
		// grass: a blade tapering to a point
		width = 1.0 - tip;
	}
	if (x > width) discard;
	// each card sits somewhere between the two suit colours; the spread and the brightness jitter grow with the variation
	float t = clamp(variation * colour_variation * 1.2, 0.0, 1.0);
	vec3 colour = mix(base_color.rgb, secondary_color.rgb, t);
	colour *= 1.0 + (shade - 0.5) * 0.7 * colour_variation;
	// dark at the root (in the shade of the cards above), full colour towards the tip
	ALBEDO = colour * mix(0.55, 1.15, smoothstep(0.0, 0.7, tip));
	ROUGHNESS = 0.95;
}
