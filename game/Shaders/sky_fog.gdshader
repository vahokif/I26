// The modern look's distance fog. The original's far clip is 600 (the world camera rebuild)
// and its sky's horizon panels show beyond it, so the map's edges are seldom seen;
// here the ground does not end but fades into the sky: past fog_begin each pixel
// is blended toward the sky's colour in its own direction (sky_modern.gdshaderinc,
// the same function the sky draws with), wholly so by fog_end, where the terrain
// is the sky and its horizon panels. Godot's own fog with aerial perspective reads
// a blurred radiance map, which smears the panels to grey and leaves a pale band.
// A full-screen pass over the opaque scene (depth texture), drawn before the other
// transparent things; the sky itself (depth at the far plane) is left alone.
shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

#include "sky_modern.gdshaderinc"

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float fog_begin = 500.0;
uniform float fog_end = 1800.0;
uniform float fog_curve = 3.0;   // steep: solid through the middle distance, gone quickly toward the end

void vertex() {
	POSITION = vec4(VERTEX.xy * 2.0, 0.5, 1.0);
}

void fragment() {
	float depth = texture(depth_tex, SCREEN_UV).r;
	if (depth <= 0.0) discard;   // the sky (reversed Z: the far plane is 0)
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	view.xyz /= view.w;
	float t = clamp((length(view.xyz) - fog_begin) / (fog_end - fog_begin), 0.0, 1.0);
	if (t <= 0.0) discard;
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(view.xyz, 0.0)).xyz);
	ALBEDO = modern_sky(dir, TIME, false);
	ALPHA = pow(t, fog_curve);
}
