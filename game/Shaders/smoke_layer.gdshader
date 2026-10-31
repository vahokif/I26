// The modern look's smoke layer, composited (SmokeLayer.cs): every smoke puff is drawn into a layer of its own, at half
// size, premultiplied and accumulated, and this full-screen pass lays the layer over the scene with the total opacity
// capped however many puffs overlap, so a column of smoke is never denser than i76_smoke_cap (about 0.65): the cars
// behind it stay in view. The layer already holds the puffs' own depth test against the scene, so what a wall or a car
// hides stays hidden. Drawn first of the transparent things; the sprites that follow (flames, glares) go over it, and the
// glares dim themselves where smoke lies in front of them (headlight_glare.gdshader, from the same layer).
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
#include "res://Shaders/smoke_cap.gdshaderinc"

uniform sampler2D layer : filter_linear, repeat_disable;

void vertex() {
	POSITION = vec4(VERTEX.xy * 2.0, 0.5, 1.0);
}

void fragment() {
	vec4 s = texture(layer, SCREEN_UV);
	if (s.a < 0.002) discard;
	float a = i76_smoke_alpha(s.a);
	ALBEDO = s.rgb / s.a * a;
	ALPHA = a;
}
