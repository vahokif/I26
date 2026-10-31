// The modern look's flamer streams (NapalmModern): one shader, three materials by `mode`.
//   0  the burning jet, a camera-facing ribbon along the stream's points: a turbulent core scrolling away from the
//      nozzle, yellow-white near it, orange then deep red toward the far end, edges fraying into licks.
//   1  the licks and embers (CPU quads): upright teardrop tongues as wreck_flame's, and small hot sparks.
//   2  the smoke: soft noisy discs, dark, lit orange low and young by the fire under them, else by the mission's light.
// Vertex colour carries: r = along (mode 0) or life 0..1 (1, 2); g = contact (0) or seed (1, 2); b = tip fade (0) or
// kind (1: 0 lick, 1 ember); a = the style's size.  Premultiplied like the wreck fire, so it reads by day and glows at night.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform int mode = 0;
uniform float scene_light = 1.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

varying vec4 v_c;

float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float n2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

void vertex() { v_c = COLOR; }

void fragment() {
	CAB_CLIP
	if (mode == 0) {
		float x = UV.x * 2.0 - 1.0;
		float t = v_c.r;
		float contact = v_c.g;
		float s = UV.y;                       // metres along the stream
		float n = n2(vec2(x * 2.2, s * 1.5 - TIME * 9.0)) * 0.6 + n2(vec2(x * 6.0 + 3.0, s * 4.0 - TIME * 17.0)) * 0.4;
		float flick = 0.88 + 0.12 * sin(TIME * 53.0 + s * 2.7) * n2(vec2(TIME * 11.0, s * 0.8));
		float body = 1.0 - abs(x);
		// ragged edges, more toward the far end and where it lies on the target
		float fray = (1.0 - n) * (0.35 + 0.55 * t + 0.3 * contact);
		float f = smoothstep(0.0, 0.4, body - fray * 0.9);
		f *= v_c.b * (1.0 - smoothstep(0.82, 1.0, t));
		float temp = clamp(f * (1.2 - t * 0.85 - contact * 0.15) * flick * (0.7 + 0.5 * body), 0.0, 1.0);
		vec3 col = mix(vec3(0.55, 0.06, 0.0), vec3(1.0, 0.36, 0.05), smoothstep(0.1, 0.45, temp));
		col = mix(col, vec3(1.0, 0.62, 0.14), smoothstep(0.45, 0.8, temp));
		col = mix(col, vec3(1.0, 0.92, 0.55), smoothstep(0.7, 1.0, temp * body) * (1.0 - t * 1.4) * (0.5 + 0.5 * n));
		col *= (0.6 + 0.6 * temp) * (0.65 + 0.55 * n);
		ALBEDO = col * f;
		ALPHA = f * 0.55;
	} else if (mode == 1) {
		float life = v_c.r, seed = v_c.g;
		if (v_c.b > 0.5) {
			vec2 d = UV * 2.0 - 1.0;
			float a = (1.0 - smoothstep(0.2, 1.0, length(d))) * (1.0 - life) * (0.7 + 0.3 * sin(TIME * 40.0 + seed * 50.0));
			ALBEDO = vec3(1.0, 0.6, 0.15) * a;
			ALPHA = a * 0.6;
		} else {
			float y = 1.0 - UV.y, x = UV.x * 2.0 - 1.0, t = TIME;
			float n = n2(vec2(UV.x * 3.0 + seed * 17.0, y * 2.2 - t * 3.1 - seed * 5.0)) * 0.65
			        + n2(vec2(UV.x * 7.0 - seed * 9.0, y * 5.0 - t * 5.7)) * 0.35;
			x += (n2(vec2(seed * 31.0, t * 1.7)) - 0.5) * 0.7 * y * y;
			float width = 0.8 * pow(1.0 - y, 0.6) * (0.6 + 0.4 * smoothstep(0.0, 0.3, y)) + 0.02;
			float body = 1.0 - abs(x) / width;
			float f = smoothstep(0.05, 0.5, body * 1.2 + (n - 0.5) * 1.1 - y * 0.9);
			f *= smoothstep(0.0, 0.1, life) * (1.0 - smoothstep(0.5, 1.0, life)) * smoothstep(0.0, 0.08, y) * (1.0 - smoothstep(0.75, 1.0, y));
			float temp = clamp(f * (1.3 - y * 0.7) * (1.0 - life * 0.5), 0.0, 1.0);
			vec3 col = mix(vec3(0.55, 0.06, 0.0), vec3(1.0, 0.36, 0.05), smoothstep(0.1, 0.45, temp));
			col = mix(col, vec3(1.0, 0.62, 0.14), smoothstep(0.55, 1.0, temp) * (0.35 + 0.65 * n));
			col *= (0.55 + 0.6 * temp) * (0.6 + 0.6 * n);
			float depth = texture(depth_tex, SCREEN_UV).r;
			vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
			f *= clamp((-vp.z / vp.w + VERTEX.z) / 0.4, 0.0, 1.0);
			ALBEDO = col * f;
			ALPHA = f * v_c.a * 0.5;
		}
	} else {
		float life = v_c.r, seed = v_c.g;
		vec2 d = UV * 2.0 - 1.0;
		float r = length(d);
		float n = n2(d * 2.5 + seed * 40.0 + vec2(0.0, -TIME * 0.4));
		float a = smoothstep(1.0, 0.25, r + (n - 0.5) * 0.6);
		a *= smoothstep(0.0, 0.12, life) * (1.0 - smoothstep(0.35, 1.0, life)) * 0.5;
		vec3 col = mix(vec3(0.04, 0.035, 0.03), vec3(0.28, 0.26, 0.24), life) * mix(0.35, 1.0, scene_light);
		col = mix(col, vec3(0.9, 0.35, 0.06) * 0.5, (1.0 - smoothstep(0.0, 0.3, life)) * 0.5);
		ALBEDO = col * a;
		ALPHA = a;
	}
}
