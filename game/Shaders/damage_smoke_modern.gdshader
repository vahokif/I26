// The modern look's damage smoke (DamageSmoke): procedural billowing puffs, the wreck smoke's family (the same
// value-noise, premultiplied soft quads, soft against geometry). INSTANCE_CUSTOM: x life 0..1, y opacity (the
// band's density), z darkness (0 steam white .. 1 oily black), w seed. Lit by a fake sphere normal from the
// sun (half-lambert, lighter top, darker underside) and tinted by the mission's ambient so night smoke is dark.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform float soft_distance = 1.5;
uniform float scene_light = 1.0;
uniform vec3 sun_view = vec3(0.3, 0.8, 0.5);   // toward the sun, in view space
uniform vec3 fog_col = vec3(0.5, 0.55, 0.6);
uniform float a_cap = 0.25;   // without the smoke layer (SmokeLayer.cs: Compatibility renderer): no one puff is more opaque than this
uniform vec3 tint = vec3(1.0);   // the chemical cloud's green (ChemClouds); white for the damage smoke

// the scene's nearest lamps, as the smoke layer has them (SmokeLayer.Lights): per light position and range, spot axis and
// angle (0: a point light), colour times energy and attenuation; the layer's own pass does the same lighting in GLSL
uniform vec4 lights[48];
uniform int light_count = 0;
varying vec4 v_c;
varying vec3 v_lit;

vec3 scatter(vec3 w, vec3 cam) {
	vec3 sum = vec3(0.0);
	vec3 toc = normalize(cam - w);
	for (int i = 0; i < light_count; i++) {
		vec4 a = lights[i * 3], b = lights[i * 3 + 1], c = lights[i * 3 + 2];
		vec3 d = w - a.xyz;
		float dist = max(length(d), 0.3);
		if (dist >= a.w) continue;
		vec3 dir = d / dist;
		float att = pow(1.0 - dist / a.w, c.w);
		if (b.w > 0.0) att *= pow(clamp(1.0 - acos(clamp(dot(dir, b.xyz), -1.0, 1.0)) / b.w, 0.0, 1.0), 0.7);
		const float g = 0.55;
		float f = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * dot(dir, toc), 1.5) / ((1.0 - g * g) / pow(1.0 - g, 3.0));
		sum += c.xyz * att * (0.12 + f);
	}
	return sum;
}

// the baked tiling fbm (R, G: two decorrelated fields; 8 cells to a tile at the base octave)
uniform sampler2D noise_tex : repeat_enable, filter_linear;

void vertex() {
	v_c = INSTANCE_CUSTOM;
	v_lit = light_count > 0 ? scatter(MODEL_MATRIX[3].xyz, INV_VIEW_MATRIX[3].xyz) : vec3(0.0);
	float sc = length(MODEL_MATRIX[0].xyz);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0] * sc, INV_VIEW_MATRIX[1] * sc, INV_VIEW_MATRIX[2] * sc, MODEL_MATRIX[3]);
}

void fragment() {
	CAB_CLIP
	float life = v_c.x, seed = v_c.w;
	vec2 d = UV * 2.0 - 1.0;
	float ang = seed * 6.2832 + life * 0.6;
	vec2 r = mat2(vec2(cos(ang), sin(ang)), vec2(-sin(ang), cos(ang))) * d;
	float rad = length(d);
	// billows: a lumpy disc, eroded by noise that bites deeper as the puff ages
	float n = texture(noise_tex, (r * 2.2 + seed * 17.0 + vec2(0.0, life * 0.8)) * 0.125).r;
	float n2b = texture(noise_tex, (r * 5.0 - seed * 9.0) * 0.125).g;
	float shape = 1.0 - rad - (n - 0.5) * 0.8 - (n2b - 0.5) * (0.5 + 0.6 * v_c.z);
	float erode = mix(0.0, 0.55, smoothstep(0.35, 1.0, life));
	float a = smoothstep(erode, erode + mix(0.7, 0.35, v_c.z), shape + 0.12);
	a *= smoothstep(0.0, 0.04, life);
	a *= 1.0 - smoothstep(0.7, 0.98, rad);   // the noise can lift the shape past the quad's edge: never let it reach it
	a *= v_c.y * (1.0 - 0.35 * life) * (0.55 + 0.9 * n);   // lumpy inside as well
	if (a < 0.004) discard;   // most of a quad's corners: no lighting, no depth read
	// fake sphere normal, half-lambert from the sun
	vec3 nrm = normalize(vec3(d.x, d.y, sqrt(max(1.0 - rad * rad, 0.05))) + vec3((n - 0.5) * 0.5));
	float hl = dot(nrm, normalize(sun_view)) * 0.5 + 0.5;
	float lit = mix(0.25 + 1.0 * hl * hl, 0.04 + 2.6 * pow(hl, 3.0), v_c.z);   // dark smoke: tops catch the sun, undersides go near-black
	vec3 base = mix(vec3(0.62, 0.64, 0.66), vec3(0.028, 0.028, 0.03), pow(v_c.z, 0.7));   // linear
	base = mix(base, base * vec3(1.35, 1.0, 0.75) + vec3(0.004, 0.002, 0.0), v_c.z * fract(seed * 7.13));   // charcoal to brown-grey
	base *= (0.8 + 0.4 * n) * tint;
	vec3 col = base * lit * scene_light;
	col += mix(vec3(0.7), vec3(0.14), pow(v_c.z, 0.7)) * tint * v_lit * (0.8 + 0.4 * n);
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	a *= clamp((-vp.z / vp.w + VERTEX.z) / soft_distance, 0.0, 1.0);
	// in the cockpit the puffs by the eye thin out: the engine's smoke comes up over the hood close to the camera and a
	// 2 to 4 m puff there fills the windscreen (the original's 0.8 m quads showed over the hood and no more)
	if (i76_cab_half.x > 0.0) a *= smoothstep(1.5, 5.0, -VERTEX.z);
	a = a_cap * (1.0 - exp(-a / a_cap));
	ALBEDO = col * a;
	ALPHA = a;
}
