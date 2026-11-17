shader_type spatial;
render_mode unshaded, cull_front, depth_test_disabled, depth_draw_never, blend_mix, shadows_disabled;
// The dust of one blast as a volume (#345, DustVolume.cs). The mesh is only a box around the dust, drawn from its inside faces so it also works with
// the camera in it. Every pixel walks its view ray through the dust and adds up what it meets, stopping at the scene's depth, so there is no element
// to make out: one cloud with a ragged, drifting edge. Three shapes, the same as DustRules: the cloud (a low dome), the column (out of its middle, with
// a wider head) and the blast wave (a low roll of dust running outwards over the ground, thickest at the ground, with a lower, thinning haze behind it).
// The wave follows the ground: `ground_tex` is the ground's height around the blast (relative to `origin`), sampled when the blast went off.
// 3D noise eats into all of them.
// Unshaded: `tint` follows the time of day.

const int CORE_STEPS = 20;
const int RING_STEPS = 12;
const int HAZE_STEPS = 8;
// the crest is this deep behind the leading edge, metres, and the edge comes up over this much
const float CREST_M = 12.0;
const float EDGE_M = 1.5;

uniform vec3 origin;
uniform vec3 tint : source_color = vec3(0.62, 0.55, 0.45);
uniform float age = 0.0;
uniform float extinction = 0.3;
uniform float noise_scale = 0.15;
uniform vec3 drift = vec3(0.0);
// the cloud and the column
uniform float core_r = 1.0;
uniform float core_top = 1.0;
uniform float cloud_r = 1.0;
uniform float cloud_h = 1.0;
uniform float cloud_a = 0.0;
uniform float col_r = 1.0;
uniform float col_top = 0.0;
uniform float col_a = 0.0;
// the blast wave (wave_reach 0: none)
uniform float wave_reach = 0.0;
uniform float wave_front = 0.0;
uniform float wave_seconds = 1.0;
uniform float wave_hang = 1.0;
uniform float wave_rise = 0.12;
uniform float wave_h = 3.0;
// the wave is the same for every blast: its own thickness and grain
uniform float wave_extinction = 0.23;
uniform float wave_noise_scale = 0.0625;
// the ground under the wave: heights relative to origin.y over a square around it; uv = xz * ground_scale + 0.5
uniform sampler2D ground_tex : filter_linear, repeat_disable;
uniform float ground_scale = 0.0;
uniform float ground_min = 0.0;
uniform float ground_max = 0.0;
uniform sampler3D noise_tex : repeat_enable, filter_linear;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// The part of a ray inside an upright cylinder of `radius` around the y axis, between heights y0 and y1: (near, far), near > far when it misses.
vec2 cylinder(vec3 ro, vec3 rd, float radius, float y0, float y1) {
	float near = -1.0e9;
	float far = 1.0e9;
	float a = dot(rd.xz, rd.xz);
	float c = dot(ro.xz, ro.xz) - radius * radius;
	if (a < 1.0e-8) {
		if (c > 0.0) { return vec2(1.0, 0.0); }
	} else {
		float b = dot(ro.xz, rd.xz);
		float disc = b * b - a * c;
		if (disc < 0.0) { return vec2(1.0, 0.0); }
		float s = sqrt(disc);
		near = (-b - s) / a;
		far = (-b + s) / a;
	}
	if (abs(rd.y) < 1.0e-6) {
		if (ro.y < y0 || ro.y > y1) { return vec2(1.0, 0.0); }
	} else {
		float ta = (y0 - ro.y) / rd.y;
		float tb = (y1 - ro.y) / rd.y;
		near = max(near, min(ta, tb));
		far = min(far, max(ta, tb));
	}
	return vec2(near, far);
}

float grain(vec3 q, float scale) {
	vec3 p = q * scale + drift;
	float n = texture(noise_tex, p).r * 0.65 + texture(noise_tex, p * 2.7 + vec3(0.37, 0.11, 0.73)).r * 0.35;
	return clamp((n - 0.5) * 1.9 + 0.5, 0.0, 1.0);
}

float core_density(vec3 q, float n) {
	float h = length(q.xz);
	float d = 0.0;
	if (cloud_a > 0.0) {
		float e = length(vec2(h / cloud_r, q.y / cloud_h));
		d = cloud_a * (1.0 - smoothstep(0.4, 1.0, e + (n - 0.5) * 0.8));
	}
	if (col_a > 0.0 && col_top > 0.1) {
		float u = q.y / col_top;
		float rad = col_r * (1.0 + 0.4 * u + 0.9 * smoothstep(0.55, 0.9, u));
		float c = col_a * (1.0 - smoothstep(0.35, 1.0, h / rad + (n - 0.5) * 0.9)) * (1.0 - smoothstep(0.8, 1.0, u + (n - 0.5) * 0.25));
		d = max(d, c);
	}
	return d;
}

// The wave's dust at q: x the density, y how much of it is the crest (1 right at the leading edge, 0 a moment behind it).
// The crest is what is seen coming: a solid, taller, lighter roll with a nearly clean leading border, however far the wave has run. Behind it the haze thins.
vec2 wave_density(vec3 q, float n) {
	float h = max(0.0, length(q.xz) + (n - 0.5) * wave_h * 0.3);
	if (h >= wave_reach) { return vec2(0.0); }
	float left = sqrt(1.0 - h / wave_reach);
	float since = age - wave_seconds * (1.0 - left);
	if (since < 0.0) { return vec2(0.0); }
	float y = q.y - texture(ground_tex, q.xz * ground_scale + 0.5).r;
	if (y < -1.0) { return vec2(0.0); }
	// the crest is measured in metres behind the edge, so it is the same at any speed; once the wave has stopped it settles
	float back = max(0.0, wave_front - h);
	float crest = exp(-back / CREST_M) * exp(-max(0.0, age - wave_seconds) / 0.35);
	float tall = wave_h * (0.45 + 0.6 * crest);
	float yn = max(y, 0.0) / tall;
	// the crest stands like a wall; the haze behind it is thickest at the ground
	float level = exp(-1.6 * yn * (1.0 - crest)) * (1.0 - smoothstep(0.7, 1.0, yn + (n - 0.5) * 0.35));
	float haze = left * exp(-since / wave_hang) * mix(0.55, 1.0, n);
	float lead = crest * max(left, 0.55) * smoothstep(0.0, 0.1, left);
	return vec2(level * smoothstep(0.0, EDGE_M, back) * max(haze, lead), crest);
}

// Walks one stretch of the ray through the wave.
void march_wave(vec3 ro, vec3 rd, float t0, float t1, int steps, float jitter, inout float clear, inout vec3 lit) {
	if (t1 <= t0 || clear < 0.02) { return; }
	float dt = (t1 - t0) / float(steps);
	float t = t0 + dt * jitter;
	for (int i = 0; i < steps; i++) {
		vec3 q = ro + rd * t;
		float n = grain(q, wave_noise_scale);
		vec2 d = wave_density(q, n);
		if (d.x > 0.002) {
			float a = 1.0 - exp(-d.x * wave_extinction * dt);
			float shade = mix(0.8, 1.04, n) * (1.0 + 0.3 * d.y);
			lit += clear * a * shade * tint;
			clear *= 1.0 - a;
			if (clear < 0.02) { return; }
		}
		t += dt;
	}
}

void fragment() {
	vec3 ro = CAMERA_POSITION_WORLD - origin;
	vec3 to = world_pos - CAMERA_POSITION_WORLD;
	float box_far = length(to);
	vec3 rd = to / max(box_far, 1.0e-4);

	float depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 behind = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	float scene_far = abs(behind.w) < 1.0e-7 ? 1.0e9 : length(behind.xyz / behind.w);
	float t_max = min(box_far, scene_far);

	float jitter = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
	float clear = 1.0;
	vec3 lit = vec3(0.0);

	vec2 core = cylinder(ro, rd, core_r, 0.0, core_top);
	core.x = max(core.x, 0.0);
	core.y = min(core.y, t_max);
	if (core.y > core.x && (cloud_a > 0.0 || col_a > 0.0)) {
		float dt = (core.y - core.x) / float(CORE_STEPS);
		float t = core.x + dt * jitter;
		for (int i = 0; i < CORE_STEPS; i++) {
			vec3 q = ro + rd * t;
			float n = grain(q, noise_scale);
			float d = core_density(q, n);
			if (d > 0.002) {
				float a = 1.0 - exp(-d * extinction * dt);
				float shade = mix(0.74, 1.08, clamp(q.y / core_top, 0.0, 1.0)) * mix(0.86, 1.06, n);
				lit += clear * a * shade * tint;
				clear *= 1.0 - a;
				if (clear < 0.02) { break; }
			}
			t += dt;
		}
	}

	if (wave_reach > 0.0 && clear >= 0.02) {
		float y0 = ground_min - 0.5;
		float y1 = ground_max + wave_h * 1.1;
		vec2 outer = cylinder(ro, rd, wave_front + wave_h * 0.5, y0, y1);
		outer.x = max(outer.x, 0.0);
		outer.y = min(outer.y, t_max);
		if (outer.y > outer.x) {
			// the ring just behind the front gets most of the steps (the crest is thin), the haze inside it the rest
			float inner_r = wave_front - CREST_M * 2.5;
			vec2 inner = inner_r > 1.0 ? cylinder(ro, rd, inner_r, y0, y1) : vec2(1.0, 0.0);
			if (inner.y <= inner.x) { inner = vec2(outer.y, outer.y); }
			inner.x = clamp(inner.x, outer.x, outer.y);
			inner.y = clamp(inner.y, outer.x, outer.y);
			march_wave(ro, rd, outer.x, inner.x, RING_STEPS, jitter, clear, lit);
			march_wave(ro, rd, inner.x, inner.y, HAZE_STEPS, jitter, clear, lit);
			march_wave(ro, rd, inner.y, outer.y, RING_STEPS, jitter, clear, lit);
		}
	}

	float alpha = 1.0 - clear;
	if (alpha < 0.004) { discard; }
	ALBEDO = lit / alpha;
	ALPHA = alpha;
}
