shader_type spatial;
// Murky water surface (WaterSurfaces.cs sets the palette, wave amplitude, flow and depth texture per body).
// The mesh is flat: the waves are a per-pixel normal (the WaterWaves sum, mirrored in wave_sum below, plus a seamless
// noise normal map sampled at two unrelated scales), so no triangle of the surface can show. The water column under a
// pixel comes from the body's depth grid (water.json) as a texture, not from the depth buffer: depth colour, the fade
// at the bank and the foam are smooth and do not follow the terrain's triangles. The water is turbid: the bed shows
// only through the first half metre. Light is Godot's own (sun highlight, reflection of the real sky), so the water
// follows the day and night cycle. One-sided: swimming is at the surface only, the camera is never under it.
render_mode blend_mix, cull_back, depth_draw_never, specular_schlick_ggx;

uniform vec4 shallow_colour : source_color = vec4(0.46, 0.46, 0.24, 1.0);  // olive, with sediment
uniform vec4 deep_colour : source_color = vec4(0.13, 0.13, 0.07, 1.0);     // dark brown-green
uniform vec4 foam_colour : source_color = vec4(0.74, 0.74, 0.66, 1.0);
uniform float murk_density = 0.55;     // per metre of water: how fast the colour goes from shallow to deep
uniform float opacity_density = 3.0;   // per metre of water: how fast the bed disappears (turbid: about half a metre)
uniform float shore_m = 0.12;          // m of depth over which the surface fades in from the bank
uniform float foam_m = 0.28;           // m of depth at the bank where foam forms
uniform float wave_amp = 0.05;         // m: amplitude of WaterWaves.Height; 0 with reduce motion
uniform float wave_time = 0.0;         // s: the WaterWaves clock (WaterSurfaces sets it; frozen with reduce motion)
uniform vec2 flow = vec2(0.0);         // m/s: river current, the hook for the flow slice (zero: static water)
uniform float normal_strength = 0.22;  // slope added by the noise normal map
uniform vec2 depth_origin = vec2(0.0);    // south-west corner of the depth grid, local metres (x east, y north = -z)
uniform vec2 depth_inv_size = vec2(0.0);  // 1 / (the grid's width and height in metres)
uniform sampler2D depth_tex : filter_linear, repeat_disable;             // r: depth in metres (the body's depth grid)
uniform sampler2D normal_tex : filter_linear_mipmap, repeat_enable;      // seamless noise normal map (rg: tilt)
uniform sampler2D foam_tex : filter_linear_mipmap, repeat_enable;        // seamless noise (r), breaks up the foam

varying vec3 world_pos;

// One travelling wave: adds weight * k * cos(phase) * dir to the gradient g of the height.
void add_wave(vec2 p, float t, vec2 dir, float k, float omega, float weight, inout vec2 g) {
	g += weight * k * cos(k * dot(dir, p) - omega * t) * dir;
}

// Gradient of the four waves of WaterWaves.Waves (direction, wavenumber, angular speed, weight). Keep in step with
// WaterWaves.cs: WaterWaves.Slope = amplitude * wave_slope.
vec2 wave_slope(vec2 p, float t) {
	vec2 g = vec2(0.0);
	add_wave(p, t, vec2(0.9494, 0.3162), 0.9, 1.1, 0.4, g);
	add_wave(p, t, vec2(0.3827, -0.9239), 1.6, 1.7, 0.3, g);
	add_wave(p, t, vec2(-0.7071, 0.7071), 2.7, 2.2, 0.2, g);
	add_wave(p, t, vec2(0.1961, 0.9806), 4.3, 3.4, 0.1, g);
	return g;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 p = world_pos.xz;
	float depth = texture(depth_tex, (vec2(p.x, -p.y) - depth_origin) * depth_inv_size).r;

	// far away the waves are smaller than a pixel: flatten them there, so the highlight does not shimmer
	float dist = length(VERTEX);
	float detail = 1.0 / (1.0 + dist * 0.015);

	// the surface slope: the shared waves, plus the noise normal map at two unrelated scales (the second one rotated,
	// so the two never line up into a visible tile), all carried along by the flow
	vec2 q = p - flow * wave_time;
	vec2 q2 = vec2(0.7986 * q.x - 0.6018 * q.y, 0.6018 * q.x + 0.7986 * q.y);
	vec2 n1 = texture(normal_tex, q * 0.131 + vec2(0.021, 0.013) * wave_time).rg * 2.0 - 1.0;
	vec2 n2 = texture(normal_tex, q2 * 0.037 + vec2(-0.008, 0.011) * wave_time).rg * 2.0 - 1.0;
	vec2 slope = (wave_amp * wave_slope(q, wave_time) + (n1 * 0.55 + n2 * 0.45) * normal_strength) * detail;
	vec3 world_n = normalize(vec3(-slope.x, 1.0, -slope.y));
	NORMAL = normalize(mat3(VIEW_MATRIX) * world_n);

	// murk: olive in the shallows, dark brown-green in the deep; the bed is gone within about half a metre
	vec3 col = mix(shallow_colour.rgb, deep_colour.rgb, 1.0 - exp(-depth * murk_density));
	float alpha = 1.0 - exp(-depth * opacity_density);
	// at a grazing angle the surface is a mirror, whatever the depth
	float ndv = clamp(dot(NORMAL, normalize(-VERTEX)), 0.0, 1.0);
	float fresnel = 0.02 + 0.98 * pow(1.0 - ndv, 5.0);
	alpha = mix(alpha, 1.0, fresnel);

	// foam along the bank, broken up by the noise; its width breathes slowly, as if the water lapped at the bank
	float fn = texture(foam_tex, q * 0.19 + vec2(0.012, 0.004) * wave_time).r;
	float lap = 0.5 + 0.5 * sin(wave_time * 0.8 + fn * 6.2832);
	float band = 1.0 - smoothstep(0.0, foam_m * (0.65 + 0.35 * lap), depth);
	float foam = band * smoothstep(0.42, 0.68, fn);
	col = mix(col, foam_colour.rgb, foam * 0.7);
	alpha = max(alpha, foam * 0.8);

	ALBEDO = col;
	ALPHA = alpha * smoothstep(0.0, shore_m, depth);
	ROUGHNESS = mix(0.3, 0.07, detail) + foam * 0.5;
	SPECULAR = 0.5;
	METALLIC = 0.0;
}
