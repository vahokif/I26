// The modern look's burning wreck (WreckFire): its smoke, one puff per particle, a camera-facing quad turned
// by the particle's angle. With the fetched Unity Labs wisp flipbook (tools/fetch-vfx.sh) each puff plays it
// through over its life; without it a noisy soft disc. Thick and near-black over the flames, thin and grey
// once the wreck only smoulders; the young puffs low down are lit orange by the fire under them, the rest by
// the mission's light (dim at night).
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
uniform bool has_atlas = false;
uniform vec2 grid = vec2(8.0, 8.0);
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float soft_distance = 1.2;

instance uniform float scene_light = 1.0;
instance uniform float fire = 1.0;    // how strongly the fire under it lights the smoke
instance uniform float thick = 1.0;   // 1 thick black smoke, 0 a smouldering grey wisp

varying float v_life;
varying float v_seed;

float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float n2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

vec2 cell(float i) { return vec2(mod(i, grid.x), floor(i / grid.x)) / grid; }

void vertex() {
	v_life = INSTANCE_CUSTOM.y;
	v_seed = fract(float(INSTANCE_ID) * 0.7548777 + INSTANCE_CUSTOM.w * 3.17);
	float c = cos(INSTANCE_CUSTOM.x), s = sin(INSTANCE_CUSTOM.x);
	VERTEX.xy = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy;
	float sc = length(MODEL_MATRIX[0].xyz);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
}

void fragment() {
	CAB_CLIP
	float a, shade;
	if (has_atlas) {
		float count = grid.x * grid.y;
		float f = clamp(v_life * (count - 1.0), 0.0, count - 1.001);
		float i0 = floor(f);
		vec2 uv = UV / grid;
		vec4 t = mix(texture(atlas, cell(i0) + uv), texture(atlas, cell(min(i0 + 1.0, count - 1.0)) + uv), f - i0);
		a = t.a;
		shade = dot(t.rgb, vec3(0.333));
	} else {
		vec2 d = UV * 2.0 - 1.0;
		float n = n2(UV * 4.0 + v_seed * 13.0 + vec2(0.0, -TIME * 0.2)) * 0.6 + n2(UV * 9.0 - v_seed * 7.0) * 0.4;
		a = smoothstep(1.0, 0.25, length(d) + (n - 0.5) * 0.7) * 0.8;
		shade = 0.6 + 0.4 * n;
	}
	a *= smoothstep(0.0, 0.08, v_life) * (1.0 - smoothstep(0.55, 1.0, v_life));
	a = min(a * mix(0.55, 1.3, thick), 1.0);
	// near-black over the flames, lifting to a dark grey as it spreads; a smoulder is a paler grey throughout
	// (linear: 0.03 is about sRGB 0.19)
	vec3 col = vec3(mix(mix(0.006, 0.035, smoothstep(0.0, 0.9, v_life)), mix(0.09, 0.16, v_life), 1.0 - thick)) * (0.75 + 0.5 * shade);
	col *= scene_light;
	// lit from below by the fire while young
	col += vec3(1.0, 0.3, 0.06) * fire * (1.0 - smoothstep(0.0, 0.2, v_life)) * (0.5 + 0.5 * shade) * 0.5;

	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	float soft = clamp((-vp.z / vp.w + VERTEX.z) / soft_distance, 0.0, 1.0);
	a *= soft;
	ALBEDO = col * a;
	ALPHA = a;
}
