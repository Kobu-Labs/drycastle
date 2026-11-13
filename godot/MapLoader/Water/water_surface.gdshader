shader_type spatial;
// Murky water surface (WaterSurfaces.cs sets the palette, wave amplitude and flow per body from WaterPalettes).
// Depth is the water column above the bed under each pixel, from the opaque scene depth: shallow water shows the
// refracted bed through an olive tint (a little sediment), deep water goes dark brown-green. The surface is waved by
// WaterWaves.Height (mirrored in wave_sum below) and two scrolling noise normal maps add the small ripples. Sky and sun
// reflection with a Fresnel term; foam forms on the shallows at the shore. Two-sided: the underside is the same water, darker.
render_mode blend_mix, cull_disabled, specular_schlick_ggx;

uniform vec4 shallow_colour : source_color = vec4(0.46, 0.46, 0.24, 1.0);  // olive, with sediment
uniform vec4 deep_colour : source_color = vec4(0.13, 0.13, 0.07, 1.0);     // dark brown-green
uniform float murk_density = 0.55;     // per metre of water: how fast the bed fades into the deep colour
uniform float wave_amp = 0.05;         // m: amplitude of WaterWaves.Height; 0 with reduce motion
uniform float wave_time = 0.0;         // s: the WaterWaves clock (WaterSurfaces sets it; frozen with reduce motion)
uniform vec2 flow = vec2(0.0);         // m/s: river current, the hook for slice 4 (zero: static water)
uniform float shore_fade_m = 1.2;      // m of depth over which the surface fades in from the bank (UnderwaterFx.ShoreFadeMetres)
uniform float foam_m = 0.25;           // m of depth at the shore where foam forms
uniform float refraction = 0.02;       // screen-space offset of the bed per unit of normal tilt, in shallows
uniform float normal_strength = 0.6;   // tilt of the noise normal maps
uniform float reflect_strength = 0.8;  // sky reflection on top of the Fresnel term
uniform vec3 sky_horizon : source_color = vec3(0.62, 0.66, 0.60);
uniform vec3 sky_zenith : source_color = vec3(0.32, 0.46, 0.60);
uniform vec3 sun_dir = vec3(0.3, 0.8, 0.5);  // world direction to the sun
uniform sampler2D ripple_tex : filter_linear_mipmap, repeat_enable;  // WaterNoiseTexture.cs: rgb tangent normal (x, z), a height
uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

// One travelling wave: adds weight * sin(phase) to h and weight * k * cos(phase) * dir to the gradient g.
void add_wave(vec2 p, float t, vec2 dir, float k, float omega, float weight, inout float h, inout vec2 g) {
	float ph = k * dot(dir, p) - omega * t;
	h += weight * sin(ph);
	g += weight * k * cos(ph) * dir;
}

// The four waves of WaterWaves.Waves (direction, wavenumber, angular speed, weight). Keep in step with WaterWaves.cs:
// WaterWaves.Height(x, z, t, a) = a * h and WaterWaves.Slope = a * g.
void wave_sum(vec2 p, float t, out float h, out vec2 g) {
	h = 0.0;
	g = vec2(0.0);
	add_wave(p, t, vec2(0.9494, 0.3162), 0.9, 1.1, 0.4, h, g);
	add_wave(p, t, vec2(0.3827, -0.9239), 1.6, 1.7, 0.3, h, g);
	add_wave(p, t, vec2(-0.7071, 0.7071), 2.7, 2.2, 0.2, h, g);
	add_wave(p, t, vec2(0.1961, 0.9806), 4.3, 3.4, 0.1, h, g);
}

void vertex() {
	// the surface is displaced by the shared wave height in world x and z; the normal is the analytic gradient
	vec2 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
	float h;
	vec2 g;
	wave_sum(wp, wave_time, h, g);
	VERTEX.y += wave_amp * h;
	NORMAL = normalize(vec3(-wave_amp * g.x, 1.0, -wave_amp * g.y));
}

void fragment() {
	// the bed under this pixel, in world space, from the opaque scene depth; the water column is the height difference
	float raw_depth = texture(DEPTH_TEXTURE, SCREEN_UV).r;
	vec4 bed_view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	bed_view.xyz /= bed_view.w;
	vec3 bed = (INV_VIEW_MATRIX * vec4(bed_view.xyz, 1.0)).xyz;
	vec3 surf = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float depth = max(surf.y - bed.y, 0.0);

	// surface detail: two noise normal maps scrolling in opposite directions, moved by the flow
	vec2 q = surf.xz - flow * wave_time;
	vec4 n1 = texture(ripple_tex, q * 0.23 + vec2(0.013, 0.021) * wave_time);
	vec4 n2 = texture(ripple_tex, q * 0.31 + vec2(-0.017, 0.011) * wave_time + vec2(0.37, 0.61));
	vec2 tilt = ((n1.rg * 2.0 - 1.0) + (n2.rg * 2.0 - 1.0)) * normal_strength;
	vec3 world_n = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
	world_n = normalize(vec3(world_n.x + tilt.x, world_n.y, world_n.z + tilt.y));
	NORMAL = normalize((mat3(VIEW_MATRIX) * world_n));

	// murk: the bed seen through the water, tinted olive in the shallows, dark brown-green in the deep
	float transmit = exp(-depth * murk_density);
	vec2 refracted_uv = SCREEN_UV + NORMAL.xy * refraction * transmit;
	vec3 bed_col = textureLod(SCREEN_TEXTURE, refracted_uv, 0.0).rgb;
	vec3 shallow = mix(shallow_colour.rgb, bed_col * shallow_colour.rgb * 1.8, 0.6);
	vec3 col = mix(deep_colour.rgb, shallow, transmit);
	// low transparency; the edge fades out over shore_fade_m of depth, so the surface melts into the bank
	float edge = clamp(depth / shore_fade_m, 0.0, 1.0);
	float alpha = mix(0.8, 0.96, 1.0 - transmit) * edge;

	// foam on the shallows at the shore, broken up by the noise height
	float foam_noise = texture(ripple_tex, q * 0.9 + vec2(0.2, 0.7) + vec2(0.02, 0.0) * wave_time).a;
	float foam = smoothstep(foam_m, 0.0, depth) * smoothstep(0.42, 0.62, foam_noise);
	col = mix(col, vec3(0.80, 0.80, 0.72), foam * 0.75);
	alpha = max(alpha, foam * 0.85);

	// sky and sun reflection, weighted by Fresnel
	vec3 V = normalize(-VERTEX);
	float ndv = clamp(dot(NORMAL, V), 0.0, 1.0);
	float fresnel = 0.02 + 0.98 * pow(1.0 - ndv, 5.0);
	vec3 R = (INV_VIEW_MATRIX * vec4(reflect(-V, NORMAL), 0.0)).xyz;
	vec3 sky = mix(sky_horizon, sky_zenith, smoothstep(0.0, 0.5, R.y));
	float sun = pow(max(dot(R, normalize(sun_dir)), 0.0), 300.0);
	vec3 reflection = sky + vec3(1.0, 0.92, 0.75) * sun * 2.0;

	if (FRONT_FACING) {
		ALBEDO = col * (1.0 - fresnel);
		EMISSION = reflection * fresnel * reflect_strength;
		ROUGHNESS = 0.12;
		SPECULAR = 0.5;
	} else {
		ALBEDO = col * 0.45;
		EMISSION = vec3(0.0);
		ROUGHNESS = 0.4;
		SPECULAR = 0.2;
	}
	METALLIC = 0.0;
	ALPHA = alpha;
}
