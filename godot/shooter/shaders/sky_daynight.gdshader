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

	// night tint: a little bluer and deeper while the stars are out
	col = mix(col, col * vec3(0.85, 0.92, 1.1), star_visibility * 0.5);

	// stars: one per hashed cell; size, brightness, tint and a very slow subtle twinkle vary per star
	if (star_visibility > 0.0 && dir.y > 0.0) {
		vec3 g = dir * 180.0;
		vec3 cell = floor(g);
		float r = hash3(cell);
		if (r > 0.993) {
			float h2 = hash3(cell + 1.7);
			float h3 = hash3(cell + 4.1);
			float size = mix(3.5, 2.0, h2);
			float point = clamp(1.0 - length(fract(g) - 0.5) * size, 0.0, 1.0);
			point *= point;
			float twinkle = 1.0 - 0.18 * (0.5 + 0.5 * sin(TIME * (0.8 + 2.0 * h3) + h2 * 6.2831));
			vec3 tint = mix(vec3(0.8, 0.88, 1.0), vec3(1.0, 0.92, 0.8), h3);
			float fade = smoothstep(0.0, 0.12, dir.y);
			col += point * star_visibility * twinkle * fade * tint * (0.5 + 0.7 * h2);
		}
	}

	// sun: soft-edged disc plus a glow that only shows while it is up
	float sd = dot(dir, normalize(sun_dir));
	float sun_up = smoothstep(-0.1, 0.1, normalize(sun_dir).y);
	col += sun_colour * pow(max(sd, 0.0), 24.0) * 0.35 * sun_up;
	col = mix(col, sun_colour * 1.4, smoothstep(0.9990, 0.9995, sd));

	// moon: soft disc and a faint halo, shown with moon_visibility
	float md = dot(dir, normalize(moon_dir));
	col += moon_colour * pow(max(md, 0.0), 48.0) * 0.12 * moon_visibility;
	col = mix(col, moon_colour * moon_visibility, smoothstep(0.9983, 0.9988, md) * moon_visibility);

	COLOR = col;
}
