shader_type spatial;
// Low-poly water surface (WaterSurfaces.cs sets the palette, wave clock, flow and depth texture per body).
// The look matches the flat-coloured, faceted map: a colour that deepens smoothly with the depth, a
// hard-edged foam strip along the bank, and real waves: the mesh is a regular triangle grid whose vertices rise and
// fall with the WaterWaves sum (wave_height below), shaded flat, one normal per triangle. The waves die out towards
// the bank (over wave_full_m of depth), so the waterline stays put. The water column comes from the body's depth grid
// (water.json) as a texture, not from the depth buffer. The water covers its whole outline, nearly opaque, so the bank
// is a hard edge where the ground rises out of it; the grid's cells that stick out past the outline are not drawn. Lit by the scene's sun and ambient light, so it follows the day and night
// cycle. One-sided: swimming is at the surface only, the camera is never under it.
render_mode blend_mix, cull_back, depth_draw_never, specular_schlick_ggx;

uniform vec4 shallow_colour : source_color = vec4(0.46, 0.46, 0.24, 1.0);  // olive, with sediment
uniform vec4 deep_colour : source_color = vec4(0.13, 0.13, 0.07, 1.0);     // dark brown-green
uniform vec4 foam_colour : source_color = vec4(0.80, 0.80, 0.72, 1.0);
uniform float deep_m = 2.2;            // m of depth at which the colour is fully the deep one
uniform float shallow_m = 0.5;         // m of depth over which the bed stops showing through
uniform float shallow_alpha = 0.82;    // opacity at the bank: the bed shows faintly through the shallows only
uniform float foam_m = 0.16;           // m of depth at the bank under which the foam strip lies
uniform float wave_amp = 0.1;          // m: amplitude of WaterWaves.Height in open water (0: a flat sheet)
uniform float wave_full_m = 0.6;       // m of depth at which the waves reach their full height (WaterPalettes.WaveFullDepth)
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

vec2 depth_uv(vec2 p) {
	return (vec2(p.x, -p.y) - depth_origin) * depth_inv_size;
}

void vertex() {
	// the vertex rides the shared wave height at its place; in the shallows the waves die out
	vec2 p = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
	float full = smoothstep(0.0, wave_full_m, textureLod(depth_tex, depth_uv(p), 0.0).r);
	VERTEX.y += wave_amp * full * wave_height(p, wave_time);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 p = world_pos.xz;
	float depth = texture(depth_tex, depth_uv(p)).r;

	// flat shading: the triangle's own normal, from the screen-space derivatives of the position, turned to the viewer
	vec3 n = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
	if (dot(n, VERTEX) > 0.0) {
		n = -n;
	}
	// far away a triangle is smaller than a pixel: flatten the shading there, so the surface does not shimmer
	float fade = length(VERTEX) * 0.008;
	NORMAL = normalize(mix(n, mat3(VIEW_MATRIX) * vec3(0.0, 1.0, 0.0), fade / (1.0 + fade)));

	// the colour goes smoothly from shallow to deep with the depth; a hard foam strip at the bank breathes slowly
	float deep = smoothstep(0.0, deep_m, depth);
	vec3 col = mix(shallow_colour.rgb, deep_colour.rgb, deep);
	float lap = sin(wave_time * 0.9 + p.x * 0.35) * sin(wave_time * 0.6 - p.y * 0.45);
	float foam = 1.0 - step(foam_m * (1.0 + 0.3 * lap), depth);

	ALBEDO = mix(col, foam_colour.rgb, foam);
	// the grid's cells reach past the outline; there the depth is zero and nothing is drawn
	ALPHA = mix(mix(shallow_alpha, 0.94, smoothstep(0.0, shallow_m, depth)), 0.9, foam) * step(0.01, depth);
	ROUGHNESS = mix(0.35, 0.9, foam);
	SPECULAR = 0.3;
	METALLIC = 0.0;
}
