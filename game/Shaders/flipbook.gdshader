// A flipbook sprite for the modern look's explosions and smoke (the Unity
// Labs CC0 sequences tools/fetch-vfx.sh fetches): a camera-facing quad,
// optionally spun, blending between neighbouring frames for smooth motion,
// faded where it meets the scene so it does not cut a line into the ground.
// Premultiplied blending serves both kinds: alpha sprites write their alpha,
// additive ones (the fireball, which has none) write zero. Bright fire keeps
// its brightness at night; smoke is dimmed by the scene's light.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform vec2 grid = vec2(5.0, 5.0);
uniform bool additive = false;
uniform float scene_light = 1.0;
uniform float soft_distance = 1.5;
// the cab clip cuts smoke away inside the player's cab; a blast is not smoke: a hit on the player's own car is seen from the cockpit
uniform bool clip_cab = true;

instance uniform float frame = 0.0;
instance uniform float opacity = 1.0;
instance uniform float spin = 0.0;
instance uniform vec3 tint : source_color = vec3(1.0);

void vertex() {
	float c = cos(spin), s = sin(spin);
	VERTEX.xy = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy;
	float sc = length(MODEL_MATRIX[0].xyz);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
}

vec2 cell_origin(float i) {
	return vec2(mod(i, grid.x), floor(i / grid.x)) / grid;
}

void fragment() {
	if (clip_cab) { CAB_CLIP }
	float count = grid.x * grid.y;
	float f = clamp(frame, 0.0, count - 1.001);
	float i0 = floor(f);
	vec2 uv = UV / grid;
	vec4 c = mix(texture(atlas, cell_origin(i0) + uv), texture(atlas, cell_origin(min(i0 + 1.0, count - 1.0)) + uv), f - i0);

	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	float soft = clamp((-vp.z / vp.w - (-VERTEX.z)) / soft_distance, 0.0, 1.0);

	vec3 col = c.rgb * tint;
	float fire = smoothstep(0.35, 0.85, max(col.r, max(col.g, col.b)));
	col *= mix(scene_light, 1.0, fire);
	if (additive) {
		ALBEDO = col * opacity * soft;
		ALPHA = 0.0;
	} else {
		float a = c.a * opacity * soft;
		ALBEDO = col * a;
		ALPHA = a;
	}
}
