shader_type spatial;
// Football leather on a crystal (FutbalitrineFx): the 32 panels of a football (12 black pentagons, 20 white hexagons, dark seams) projected from the middle of every crystal of the rock onto its faces.
// The panels are the cells of the 32 directions below (the 12 corners of an icosahedron first, then its 20 face centres). ball = the mesh is a sphere about its own origin.

uniform vec3 centers[10];          // middle of every crystal (model space), by the crystal number in UV2.x
uniform bool ball = false;
uniform vec3 white : source_color = vec3(0.93, 0.93, 0.9);
uniform vec3 black : source_color = vec3(0.04, 0.04, 0.05);
uniform vec3 seam_color : source_color = vec3(0.25, 0.25, 0.25);
uniform float seam = 0.012;
uniform float squash = 0.55;       // < 1 squeezes the directions of a tall crystal so the panels spread along it
uniform float rough = 0.3;

const vec3 DIRS[32] = vec3[32](
	vec3(0.00000, 0.52573, 0.85065),
	vec3(0.52573, 0.85065, 0.00000),
	vec3(0.85065, 0.00000, 0.52573),
	vec3(0.00000, 0.52573, -0.85065),
	vec3(0.52573, -0.85065, 0.00000),
	vec3(-0.85065, 0.00000, 0.52573),
	vec3(0.00000, -0.52573, 0.85065),
	vec3(-0.52573, 0.85065, 0.00000),
	vec3(0.85065, 0.00000, -0.52573),
	vec3(0.00000, -0.52573, -0.85065),
	vec3(-0.52573, -0.85065, 0.00000),
	vec3(-0.85065, 0.00000, -0.52573),
	vec3(0.57735, 0.57735, 0.57735),
	vec3(0.57735, 0.57735, -0.57735),
	vec3(0.57735, -0.57735, 0.57735),
	vec3(0.57735, -0.57735, -0.57735),
	vec3(-0.57735, 0.57735, 0.57735),
	vec3(-0.57735, 0.57735, -0.57735),
	vec3(-0.57735, -0.57735, 0.57735),
	vec3(-0.57735, -0.57735, -0.57735),
	vec3(0.00000, 0.35682, 0.93417),
	vec3(0.35682, 0.93417, 0.00000),
	vec3(0.93417, 0.00000, 0.35682),
	vec3(0.00000, 0.35682, -0.93417),
	vec3(0.35682, -0.93417, 0.00000),
	vec3(-0.93417, 0.00000, 0.35682),
	vec3(0.00000, -0.35682, 0.93417),
	vec3(-0.35682, 0.93417, 0.00000),
	vec3(0.93417, 0.00000, -0.35682),
	vec3(0.00000, -0.35682, -0.93417),
	vec3(-0.35682, -0.93417, 0.00000),
	vec3(-0.93417, 0.00000, -0.35682)
);

varying vec3 lpos;
varying float isl;

void vertex() {
	lpos = VERTEX;
	isl = UV2.x;
}

void fragment() {
	vec3 c = ball ? vec3(0.0) : centers[clamp(int(round(isl)), 0, 9)];
	vec3 d = normalize((lpos - c) * (ball ? vec3(1.0) : vec3(1.0, squash, 1.0)));
	float best = -2.0, second = -2.0;
	int who = 0;
	for (int i = 0; i < 32; i++) {
		float x = dot(d, DIRS[i]);
		if (x > best) { second = best; best = x; who = i; }
		else if (x > second) { second = x; }
	}
	vec3 col = who < 12 ? black : white;
	ALBEDO = mix(seam_color, col, smoothstep(seam * 0.5, seam, best - second));
	ROUGHNESS = rough;
	SPECULAR = 0.6;
}
