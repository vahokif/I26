// The modern look's distance fog, and the haze lying over the land. The original's far clip is 600 (the world
// camera rebuild) and its sky's horizon panels show beyond it, so the map's edges are seldom seen; here the
// ground does not end but fades into the sky: past fog_begin each pixel is blended toward the sky's colour in
// its own direction (sky_modern.gdshaderinc, the same function the sky draws with, less its clouds and its sun:
// modern_air), wholly so by fog_end, where the terrain is the sky and its horizon panels. Godot's own fog with aerial perspective reads a blurred
// radiance map, which smears the panels to grey and leaves a pale band.
// Over that sits the haze (haze.gdshaderinc, docs/desert-haze.md): the air is denser low than high, so it lies
// in the valleys and the plains and thins off up the peaks, and it lights up toward the sun. It asks for far
// less than the clip does, so it starts where the ground does and not at fog_begin, and it is what makes a
// distant horizon read as distance rather than as a flat painting of one.
// A full-screen pass over the opaque scene (depth texture), drawn before the other transparent things; the sky
// itself (depth at the far plane) is left alone: the sky already carries its own sun and its own glow.
shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

#include "sky_modern.gdshaderinc"
#include "haze.gdshaderinc"

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
	float dist = length(view.xyz);
	vec3 dir = normalize((INV_VIEW_MATRIX * vec4(view.xyz, 0.0)).xyz);
	float t = clamp((dist - fog_begin) / (fog_end - fog_begin), 0.0, 1.0);
	float alpha = pow(t, fog_curve);
	// the air between here and there, counted along the ray: more of it down in the valleys, less up high
	float tau = i76_haze_depth((INV_VIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).y, dir, dist);
	if (tau > 0.0) alpha = 1.0 - (1.0 - alpha) * (1.0 - (1.0 - exp(-tau)));   // either way of going to sky, not one stacked on the other
	if (alpha <= 0.002) discard;   // clear air: there is nothing here to lay over the scene
	float towards = max(dot(dir, i76_haze_sun), 0.0);
	// the air's own colour, not the sky's: modern_air is that sky with its clouds and its disc out of it, because
	// scattered light is smooth and a cloud's shape laid across distant ground reads as a cloud seen
	// through the mountain in front of it
	ALBEDO = modern_air(dir, TIME, false) + i76_haze_tint * (i76_haze_mie * i76_haze_phase(towards));
	ALPHA = alpha;
}
