// A spinning helicopter rotor in the modern look: the blades seen as dark bars while slow, smearing into a
// translucent disc as they spin up (motion blur: alpha strongest toward the tips, faint streaks, a slight
// shimmer). A flat quad on the rotor's plane; UV is the disc, `angle` the blade phase, `blur` 0 (stopped:
// the blades) to 1 (a full smear).
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never, shadows_disabled, fog_disabled;

uniform int blades = 2;
uniform float hub = 0.08;
uniform float blade_width = 0.03;
uniform vec3 tone : source_color = vec3(0.16, 0.17, 0.17);
uniform float scene_light = 1.0;

instance uniform float angle = 0.0;
instance uniform float blur = 0.0;

void fragment() {
	CAB_CLIP
	vec2 p = UV * 2.0 - 1.0;
	float r = length(p);
	if (r > 1.0 || r < hub) discard;
	float a = atan(p.y, p.x) - angle;
	float tau = 6.28318530718;
	float nb = float(blades);
	float s = fract(a * nb / tau);
	float perp = min(s, 1.0 - s) * tau / nb * r;
	float tip = 1.0 - smoothstep(0.93, 1.0, r);
	float root = smoothstep(hub, hub + 0.06, r);
	// the blades, sharp: a bar of constant width, a little wider at the root
	float bar = (1.0 - smoothstep(blade_width * 0.6, blade_width, perp)) * tip * root;
	// the smear: grows toward the tips, trails behind each blade, faint streaks, a shimmer
	float trail = exp(-s * 3.0 * nb * 0.5) ;
	float streak = 0.75 + 0.25 * sin(a * 31.0 + r * 9.0) * sin(a * 7.0 - r * 3.0);
	float shimmer = 1.0 + 0.10 * sin(TIME * 47.0 + a * 3.0 + r * 11.0);
	float radial = 0.14 + 0.42 * smoothstep(0.3, 0.95, r);
	float smear = radial * streak * shimmer * (0.7 + 0.3 * trail) * tip * root;
	float alpha = mix(bar * 0.9, smear + bar * 0.25, blur);
	if (alpha < 0.004) discard;
	vec3 dark = vec3(0.05);
	ALBEDO = mix(dark, tone, blur * (1.0 - bar)) * scene_light;
	ALPHA = clamp(alpha, 0.0, 1.0);
}
