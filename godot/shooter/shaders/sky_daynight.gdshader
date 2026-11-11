shader_type sky;

// Flat day/night sky, driven by DayNightCycle.cs (see DayCurve in ShooterCore for the numbers).
uniform vec3 sky_top : source_color = vec3(0.33, 0.56, 0.9);
uniform vec3 sky_horizon : source_color = vec3(0.72, 0.84, 0.95);
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_colour : source_color = vec3(1.0, 0.97, 0.9);
uniform vec3 moon_dir = vec3(0.0, -1.0, 0.0);
uniform vec3 moon_colour : source_color = vec3(0.6, 0.7, 0.95);
uniform float moon_visibility = 0.0;
uniform float star_visibility = 0.0;

// cheap hash for star placement; one star per cell of a 180 cell grid over the sphere
float hash3(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.1));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 col = mix(sky_horizon, sky_top, pow(up, 0.6));

	// stars: a few cells per 180 across get a point, fading toward the cell edge
	if (star_visibility > 0.0 && dir.y > 0.0) {
		vec3 g = dir * 180.0;
		vec3 cell = floor(g);
		float r = hash3(cell);
		float point = step(0.993, r) * clamp(1.0 - length(fract(g) - 0.5) * 4.0, 0.0, 1.0);
		col += point * star_visibility * vec3(0.9, 0.93, 1.0) * (0.6 + 0.4 * hash3(cell + 1.7));
	}

	// flat sun and moon discs
	float sd = dot(dir, normalize(sun_dir));
	col = mix(col, sun_colour * 1.4, step(0.9993, sd));
	float md = dot(dir, normalize(moon_dir));
	col = mix(col, moon_colour * moon_visibility, step(0.9986, md) * moon_visibility);

	COLOR = col;
}
