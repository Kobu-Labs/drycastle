shader_type spatial;
// Low-poly water surface (WaterSurfaces.cs sets the palette, wave clock, flow and depth texture per body).
// The look matches the flat-coloured, faceted map: three flat depth bands (shallow, middle, deep) with hard edges, a
// hard-edged foam strip along the bank, and flat-shaded facets that rock with the waves. The mesh itself is flat; the
// facets are a regular triangle grid worked out per pixel from the WaterWaves sum (wave_height below), so they are
// even everywhere and cost no vertices. The water column under a pixel comes from the body's depth grid (water.json)
// as a texture, not from the depth buffer. The water covers its whole outline, nearly opaque, so the bank is a hard
// edge where the ground rises out of it. Lit by the scene's sun and ambient light, so it follows the day and night
// cycle. One-sided: swimming is at the surface only, the camera is never under it.
render_mode blend_mix, cull_back, depth_draw_never, specular_schlick_ggx;

uniform vec4 shallow_colour : source_color = vec4(0.46, 0.46, 0.24, 1.0);  // olive, with sediment
uniform vec4 deep_colour : source_color = vec4(0.13, 0.13, 0.07, 1.0);     // dark brown-green
uniform vec4 foam_colour : source_color = vec4(0.80, 0.80, 0.72, 1.0);
uniform float shallow_m = 0.5;         // m of depth: the shallow band ends here
uniform float deep_m = 1.6;            // m of depth: the deep band starts here (the middle band lies between)
uniform float shallow_alpha = 0.82;    // the bed shows faintly through the shallow band only
uniform float foam_m = 0.16;           // m of depth at the bank under which the foam strip lies
uniform float facet_m = 3.0;           // m: side of the facet grid's cells (each cell is two triangles)
uniform float facet_strength = 0.6;    // tilt of the facets (0: a flat sheet)
uniform float wave_time = 0.0;         // s: the WaterWaves clock (WaterSurfaces sets it; frozen with reduce motion)
uniform vec2 flow = vec2(0.0);         // m/s: river current, the hook for the flow slice (zero: static water)
uniform vec2 depth_origin = vec2(0.0);    // south-west corner of the depth grid, local metres (x east, y north = -z)
uniform vec2 depth_inv_size = vec2(0.0);  // 1 / (the grid's width and height in metres)
uniform sampler2D depth_tex : filter_linear, repeat_disable;  // r: depth in metres (the body's depth grid)

varying vec3 world_pos;

// The four waves of WaterWaves.Waves (direction, wavenumber, angular speed, weight), for unit amplitude. Keep in step
// with WaterWaves.cs: WaterWaves.Height = amplitude * wave_height.
float wave_height(vec2 p, float t) {
	return 0.4 * sin(0.9 * dot(vec2(0.9494, 0.3162), p) - 1.1 * t)
		+ 0.3 * sin(1.6 * dot(vec2(0.3827, -0.9239), p) - 1.7 * t)
		+ 0.2 * sin(2.7 * dot(vec2(-0.7071, 0.7071), p) - 2.2 * t)
		+ 0.1 * sin(4.3 * dot(vec2(0.1961, 0.9806), p) - 3.4 * t);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 p = world_pos.xz;
	float depth = texture(depth_tex, (vec2(p.x, -p.y) - depth_origin) * depth_inv_size).r;

	// the facet under this pixel: the cell of a regular grid, split along its diagonal into two triangles; the facet's
	// slope is that of the plane through the wave heights at its three corners, so the whole triangle shades as one
	vec2 g = (p - flow * wave_time) / facet_m;
	vec2 cell = floor(g);
	vec2 f = g - cell;
	bool lower = f.x + f.y < 1.0;
	vec2 a = (lower ? cell : cell + vec2(1.0, 1.0)) * facet_m;
	vec2 bx = (lower ? cell + vec2(1.0, 0.0) : cell + vec2(0.0, 1.0)) * facet_m;
	vec2 bz = (lower ? cell + vec2(0.0, 1.0) : cell + vec2(1.0, 0.0)) * facet_m;
	float ha = wave_height(a, wave_time);
	vec2 slope = vec2(wave_height(bx, wave_time) - ha, wave_height(bz, wave_time) - ha) * (lower ? 1.0 : -1.0);
	// far away a facet is smaller than a pixel: flatten them there, so the surface does not shimmer
	slope *= facet_strength / (1.0 + length(VERTEX) * 0.01);
	NORMAL = normalize(mat3(VIEW_MATRIX) * normalize(vec3(-slope.x, 1.0, -slope.y)));

	// three flat depth bands with hard edges, and a hard foam strip at the bank whose width breathes slowly
	float mid = step(shallow_m, depth);
	float deep = step(deep_m, depth);
	vec3 col = mix(shallow_colour.rgb, mix(shallow_colour.rgb, deep_colour.rgb, 0.5), mid);
	col = mix(col, deep_colour.rgb, deep);
	float lap = sin(wave_time * 0.9 + p.x * 0.35) * sin(wave_time * 0.6 - p.y * 0.45);
	float foam = 1.0 - step(foam_m * (1.0 + 0.3 * lap), depth);

	ALBEDO = mix(col, foam_colour.rgb, foam);
	ALPHA = mix(mix(shallow_alpha, 0.94, mid), 0.9, foam);
	ROUGHNESS = mix(0.35, 0.9, foam);
	SPECULAR = 0.3;
	METALLIC = 0.0;
}
