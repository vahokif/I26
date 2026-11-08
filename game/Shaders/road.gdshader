// Roads. The engine draws them in its painter's sort after the ground, so
// they never fight it; with a depth buffer they would. The quad is pulled a
// little toward the camera along the view ray (a fraction of its depth,
// plus a few centimetres), which keeps it on top of the terrain it lies on
// without visibly lifting it.
//
// 1997 mode: unshaded, the engine's road light: one flat value,
// max(0, I*Ly) + ambient, with no normal and no depth term (the terrain and
// the objects have the depth fade, the roads do not), through the mission's
// light ramp like the terrain.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D road_tex : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform bool mode_1997 = false;
uniform float light_y = 0.2588;
uniform float ambient = 0.0;
uniform float pull = 0.004;

// the engine's light ramp (the mission's .act and .lum): a vertex lit `light` takes LUMA[round(31·light)]
#include "res://Shaders/ramp.gdshaderinc"
#include "res://Shaders/software.gdshaderinc"

varying vec2 v_intensity;

vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 to_linear(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float depth = -view.z;
	v_intensity = i76_ramp(max(light_y, 0.0) + ambient);
	// fade the pull in with distance: right at the eye a proportional pull
	// would drag nearby road up into the view
	view.xyz *= 1.0 - (pull + 0.12 / max(depth, 1.0)) * smoothstep(1.5, 12.0, depth);
	POSITION = PROJECTION_MATRIX * view;
}

void fragment() {
	vec4 t = mode_1997 && i76_software ? i76_point(road_tex, UV) : texture(road_tex, UV);
	if (mode_1997) t = i76_tex(t);
	if (t.a < 0.5) discard;
	if (mode_1997) {
		ALBEDO = to_linear(i76_shade(to_srgb(t.rgb), v_intensity));
		EMISSION = i76_out(ALBEDO);
		ALBEDO = vec3(0.0);
		ROUGHNESS = 1.0;
		SPECULAR = 0.0;
	} else {
		ALBEDO = t.rgb;
		ROUGHNESS = 0.9;
	}
}
