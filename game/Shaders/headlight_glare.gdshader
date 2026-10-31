// The modern look's lamp glare: a camera-facing quad at the lamp that
// lights up as the camera comes into its beam. Headlamps use it narrow and
// warm; the rear lamps (brake and tail) wide and red, their brightness set
// per instance (level). The node's -Z is the lamp's
// forward. A hot core, a soft halo and a horizontal streak, all scaled by how
// squarely the lamp faces the camera, grown with distance so a far car still
// flares, and toned down in daylight (the sun's intensity). It is drawn
// additively with no depth test, so it never slices into the ground or the
// car's own front; instead the lamp's own point is tested against the depth
// buffer (a few taps round it), and the glare fades when something stands in
// front of the lamp.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled;

global uniform float i76_light_intensity;
global uniform sampler2D i76_smoke : filter_nearest;
#include "res://Shaders/smoke_cap.gdshaderinc"
uniform vec3 tint : source_color = vec3(1.0, 0.93, 0.78);
uniform float strength = 0.75;
uniform float sharpness = 4.0;     // how narrow the beam is: the facing term's power
uniform float wide = 0.0;          // a floor under the facing term, for lamps seen from the side
uniform float size_near = 0.2;
uniform float size_far = 1.0;
uniform float streak_amount = 0.45;
uniform float halo_amount = 0.18;
uniform float day_floor = 0.25;    // how much is left at full sun
instance uniform float level = 1.0;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

varying float facing;
varying vec3 lamp_view;     // the lamp in view space, for the occlusion test

void vertex() {
	vec3 lamp = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	vec3 fwd = normalize((MODEL_MATRIX * vec4(0.0, 0.0, -1.0, 0.0)).xyz);
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	vec3 to_cam = cam - lamp;
	float dist = length(to_cam);
	to_cam /= max(dist, 1e-4);
	facing = max(dot(fwd, to_cam), 0.0);
	float size = mix(size_near, size_far, facing) * clamp(dist * 0.04, 1.0, 4.0);
	lamp_view = (VIEW_MATRIX * vec4(lamp, 1.0)).xyz;
	vec3 world = lamp + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * size + to_cam * 0.45;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float r2 = dot(p, p);
	// everything fades to nothing before the quad's edge, so no outline shows against a dark sky
	float edge = 1.0 - smoothstep(0.55, 0.98, sqrt(r2));
	float core = exp(-r2 * 22.0);
	float halo = exp(-r2 * 4.5) * halo_amount * edge;
	float streak = exp(-p.y * p.y * 300.0) * exp(-abs(p.x) * 2.6) * streak_amount * facing * (1.0 - smoothstep(0.6, 0.98, abs(p.x)));
	float aim = max(pow(facing, sharpness), wide * step(0.0001, facing));
	float day = mix(1.0, day_floor, clamp(i76_light_intensity, 0.0, 1.0));
	// is the lamp itself in view? its depth against the scene's, at and round its point on screen
	vec4 clip = PROJECTION_MATRIX * vec4(lamp_view, 1.0);
	vec2 suv = clip.xy / clip.w * 0.5 + 0.5;
	float seen = 0.0;
	vec2 px = 3.0 / VIEWPORT_SIZE;
	for (int i = 0; i < 5; i++) {
		vec2 o = i == 0 ? vec2(0.0) : vec2(i == 1 ? 1.0 : i == 2 ? -1.0 : 0.0, i == 3 ? 1.0 : i == 4 ? -1.0 : 0.0) * px;
		float d = texture(depth_tex, suv + o).r;
		vec4 v = INV_PROJECTION_MATRIX * vec4((suv + o) * 2.0 - 1.0, d, 1.0);
		float scene = -v.z / v.w;
		seen += step(-lamp_view.z - 0.35, scene);
	}
	seen /= 5.0;
	if (clip.w <= 0.0 || any(lessThan(suv, vec2(0.0))) || any(greaterThan(suv, vec2(1.0)))) seen = 0.0;
	// the smoke layer (smoke_layer.gdshader) is drawn under this: dim the glare where smoke lies in front of the lamp
	vec4 sm = texture(i76_smoke, SCREEN_UV);
	float veil = sm.a > 0.002 && sm.r < -lamp_view.z * sm.a ? i76_smoke_alpha(sm.a) : 0.0;
	ALBEDO = tint * (core + halo + streak) * aim * strength * day * seen * level * (1.0 - veil);
}
