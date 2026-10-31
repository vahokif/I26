// The modern Ball O'Flame's tail (ModernFx.Fireball): flame cards from the fireball flipbook, one MultiMesh instance each,
// turned to face the camera, additive, cooling from orange to dark red as they age and fading out. The instance's custom
// data holds its age (0 new, 1 gone) and its frame, its basis the size. Soft against the ground, clipped out of the cab.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_add, fog_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float scene_light = 1.0;

varying vec3 life_frame;

void vertex() {
	float spin = INSTANCE_CUSTOM.z;
	float c = cos(spin), s = sin(spin);
	VERTEX.xy = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy;
	float sc = length(MODEL_MATRIX[0].xyz);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
	life_frame = INSTANCE_CUSTOM.xyz;
}

vec2 cell_origin(float i) {
	return vec2(mod(i, 8.0), floor(i / 8.0)) / 8.0;
}

void fragment() {
	CAB_CLIP
	float f = clamp(life_frame.y, 0.0, 62.999);
	float i0 = floor(f);
	vec2 uv = UV / 8.0;
	vec3 t = mix(texture(atlas, cell_origin(i0) + uv).rgb, texture(atlas, cell_origin(i0 + 1.0) + uv).rgb, f - i0);
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	float soft = clamp((-vp.z / vp.w - (-VERTEX.z)) / 1.0, 0.0, 1.0);
	float l = clamp(life_frame.x, 0.0, 1.0);
	vec3 ramp = mix(vec3(1.0, 0.5, 0.2), vec3(0.55, 0.08, 0.03), l);
	float fade = 0.8 * (1.0 - l) * (1.0 - l);
	ALBEDO = t * ramp * fade * soft;
}
