// The modern look's muzzle flash: three planes crossed along the barrel for
// the flame tongue and one across it for the star seen from the front, all
// additive. The mesh's -Z is the barrel's forward; COLOR.r marks the star
// plane. Each plane fades as it turns edge-on to the camera, so from the side
// the tongue shows and from the front the star, with no hard plane edges.
// Where a plane meets the car or the ground it fades against the depth
// buffer. Every shot gets its own seed (the star's petals and turn, the tongue's
// licks) and burns out over its short life (age 0..1).
shader_type spatial;
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
instance uniform float age = 0.0;
instance uniform float seed = 0.0;
instance uniform float energy = 2.5;
uniform vec3 hot : source_color = vec3(1.0, 0.96, 0.82);
uniform vec3 warm : source_color = vec3(1.0, 0.62, 0.18);
uniform vec3 ember : source_color = vec3(0.85, 0.22, 0.04);

varying float star;

void vertex() {
	star = COLOR.r;
	// the tongue shoots out a little further as it burns
	if (star < 0.5) VERTEX.z *= mix(0.75, 1.15, sqrt(age));
}

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

// white at the hottest, through yellow-orange to a red rim
vec3 ramp(float t) {
	t = clamp(t, 0.0, 1.0);
	return t > 0.55 ? mix(warm, hot, (t - 0.55) / 0.45) : mix(ember, warm, t / 0.55);
}

void fragment() {
	float facing = abs(dot(normalize(NORMAL), VIEW));
	float life = 1.0 - age * age;   // holds, then burns out
	float heat;
	if (star > 0.5) {
		// the star: a hot core, 4–6 seeded spikes, a soft halo
		vec2 p = UV * 2.0 - 1.0;
		float r = length(p);
		float petals = 4.0 + floor(hash(vec2(seed, 1.7)) * 3.0);
		float a = atan(p.y, p.x) + seed * 6.2832;
		float spikes = pow(clamp(abs(cos(a * petals * 0.5)), 1e-4, 1.0), 10.0) * (0.7 + 0.3 * noise(vec2(a * 3.0, seed * 9.0)));
		float core = exp(-r * r * 22.0);
		float rays = spikes * exp(-r * 3.2) * (1.0 - smoothstep(0.6, 1.0, r));
		float halo = exp(-r * r * 5.0) * 0.25 * (1.0 - smoothstep(0.7, 1.0, r));
		heat = core * 1.4 + rays * 0.9 + halo;
		heat *= smoothstep(0.15, 0.7, facing);
	} else {
		// the tongue: u from the muzzle (0) to the tip (1), v across
		float u = UV.x, v = UV.y * 2.0 - 1.0;
		float width = mix(0.3, 1.0, smoothstep(0.0, 0.3, u)) * (1.0 - 0.8 * smoothstep(0.45, 1.0, u));
		float lick = noise(vec2(u * 5.0 - seed * 13.0, v * 2.5 + seed * 7.0));
		float vv = v / max(width, 0.05) + (lick - 0.5) * 0.6;
		float body = exp(-vv * vv * 2.6);
		float fall = pow(clamp(1.0 - u, 0.0, 1.0), 1.4) * (0.55 + 0.45 * noise(vec2(u * 9.0 - seed * 21.0, seed)));
		float core = exp(-v * v * 30.0) * (1.0 - smoothstep(0.0, 0.45, u));
		heat = (body * fall + core * 0.8) * smoothstep(0.0, 0.06, u) * (1.0 - smoothstep(0.7, 1.0, abs(v)));
		heat *= smoothstep(0.1, 0.55, facing);
	}
	// soft where it meets the car or the ground, rather than a hard line through the glow
	float d = texture(depth_tex, SCREEN_UV).r;
	vec4 sv = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(d), 1.0);
	heat *= clamp((-sv.z / sv.w + VERTEX.z) / 0.25, 0.0, 1.0);
	ALBEDO = ramp(heat * (0.6 + 0.4 * life)) * heat * energy * life;
}
