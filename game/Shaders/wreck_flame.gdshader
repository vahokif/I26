// The modern look's burning wreck (WreckFire): one flame tongue per particle, an upright billboard whose
// bottom sits where the particle is, its shape a teardrop eaten by noise scrolling upward, swaying more toward
// the tip. Hot yellow-orange low in the core, orange, then deep red at the edges, the top and with age.
// Premultiplied: part laid over the scene, part added, so it reads in daylight and glows at night.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float soft_distance = 0.5;
// how strong the fire is, 0..1: the tongues smaller and redder as it dies down
instance uniform float level = 1.0;

varying float v_life;
varying float v_seed;

float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float n2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

void vertex() {
	v_life = INSTANCE_CUSTOM.y;
	v_seed = fract(float(INSTANCE_ID) * 0.6180339 + INSTANCE_CUSTOM.w * 7.31);
	// turned to the camera about world up, so the tongues point up whichever way the camera looks
	vec3 up = vec3(0.0, 1.0, 0.0);
	vec3 back = INV_VIEW_MATRIX[2].xyz;
	vec3 side = normalize(cross(up, abs(back.y) > 0.98 ? INV_VIEW_MATRIX[1].xyz : back));
	vec3 fwd = cross(side, up);
	float sc = length(MODEL_MATRIX[0].xyz) * mix(0.45, 1.0, level);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(vec4(side * sc, 0.0), vec4(up * sc, 0.0), vec4(fwd * sc, 0.0), MODEL_MATRIX[3]);
}

void fragment() {
	CAB_CLIP
	float y = 1.0 - UV.y;            // 0 at the base, 1 at the tip
	float x = UV.x * 2.0 - 1.0;
	float t = TIME;
	float n = n2(vec2(UV.x * 3.0 + v_seed * 17.0, y * 2.2 - t * 3.1 - v_seed * 5.0)) * 0.65
	        + n2(vec2(UV.x * 7.0 - v_seed * 9.0, y * 5.0 - t * 5.7)) * 0.35;
	x += (n2(vec2(v_seed * 31.0, t * 1.7)) - 0.5) * 0.7 * y * y;
	float width = 0.8 * pow(1.0 - y, 0.6) * (0.6 + 0.4 * smoothstep(0.0, 0.3, y)) + 0.02;
	float body = 1.0 - abs(x) / width;
	// fading upward, the noise breaking it into licks and tips
	float f = smoothstep(0.05, 0.5, body * 1.2 + (n - 0.5) * 1.1 - y * 0.9);
	f *= smoothstep(0.0, 0.1, v_life) * (1.0 - smoothstep(0.5, 1.0, v_life)) * smoothstep(0.0, 0.08, y) * (1.0 - smoothstep(0.75, 1.0, y));
	float temp = clamp(f * (1.3 - y * 0.7) * (1.0 - v_life * 0.5) * mix(0.55, 1.0, level), 0.0, 1.0);
	vec3 col = mix(vec3(0.55, 0.06, 0.0), vec3(1.0, 0.36, 0.05), smoothstep(0.1, 0.45, temp));
	// the core stays yellow-orange (never white: many tongues overlap and add), with the noise's gaps showing through it
	col = mix(col, vec3(1.0, 0.62, 0.14), smoothstep(0.55, 1.0, temp) * (0.35 + 0.65 * n));
	col *= (0.55 + 0.6 * temp) * (0.6 + 0.6 * n);

	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	float soft = clamp((-vp.z / vp.w + VERTEX.z) / soft_distance, 0.0, 1.0);
	float a = f * soft;
	ALBEDO = col * a;
	ALPHA = a * 0.5;
}
