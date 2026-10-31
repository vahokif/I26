// The damage smoke (DamageSmoke): one camera-facing quad per puff from a MultiMesh. INSTANCE_CUSTOM: x frame
// (0..11, the column of the atlas), y kind (0 white, 1 grey, 2 black: the row), z life 0..1, w unused.
// 1997: point-sampled, constant 0.6 alpha, no lighting, no fog (the original's flags 0x8c: Render prim 7,
// white vertices with alpha 0x99). Modern: filtered, softened where it meets geometry, tinted by the mission's
// light, easing out at the end of its life.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_mix, fog_disabled;

uniform sampler2D atlas_near : source_color, filter_nearest, repeat_disable;
uniform sampler2D atlas_lin : source_color, filter_linear, repeat_disable;
uniform vec2 grid = vec2(12.0, 3.0);
uniform vec2 texel = vec2(64.0, 64.0);
uniform bool modern = true;
uniform float scene_light = 1.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float soft_distance = 0.8;
uniform float stretch_x = 1.0;   // the chemical cloud's puffs (kind 3) are 1.7 times wider

varying vec3 v_c;

void vertex() {
	v_c = INSTANCE_CUSTOM.xyz;
	float sc = length(MODEL_MATRIX[0].xyz);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc * stretch_x, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
}

void fragment() {
	CAB_CLIP
	// the original draws the texture transposed: (0,0) top-left, (1,0) bottom-left
	vec2 uv = vec2(UV.y, UV.x);
	vec4 t;
	if (modern) {
		vec2 e = 0.5 / texel;
		t = texture(atlas_lin, (vec2(v_c.x, v_c.y) + clamp(uv, e, 1.0 - e)) / grid);
	} else {
		t = texture(atlas_near, (vec2(v_c.x, v_c.y) + uv) / grid);
	}
	float a = t.a * 0.6;
	vec3 col = t.rgb;
	if (modern) {
		a *= 1.0 - smoothstep(0.8, 1.0, v_c.z);
		col *= scene_light;
		float depth = texture(depth_tex, SCREEN_UV).r;
		vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
		a *= clamp((-vp.z / vp.w + VERTEX.z) / soft_distance, 0.0, 1.0);
	}
	ALBEDO = col;
	ALPHA = a;
}
