// The ground past the map's tiles in the 1997 look as the map editor draws it (OutsideTerrain's chunks past 600 m, outside_1997.gdshader,
// and the distant ring, horizon_1997.gdshader): the modern look's geometry with the map's own 1997 terrain light (terrain_faithful.gdshader),
// so the editor shows the whole of it in that look, with no 600 m clip. Missions keep the original's clip (OutsideTerrain).
//
// The engine's light per vertex, from the gradient the vertex's normal carries (the meshes' normals are (-dhdx, 1, dhdz) normalised,
// in Godot's frame), through the mission's light ramp; the ground texture at the vertex's sample coordinates (game metres / 5).
// Positions are relative to the node, which stands at `centre` (the outside ground's centre, game frame XZ).
uniform sampler2D ground_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 light_game = vec3(0.9659, 0.2588, 0.0);
uniform float ambient = 0.0;
uniform float fade_a = 0.000417;
uniform float fade_b = 0.7496;
uniform float samples_per_texture = 8.0;
uniform bool show_light_only = false;
uniform vec2 centre = vec2(0.0);

#include "res://Shaders/ramp.gdshaderinc"
#include "res://Shaders/software.gdshaderinc"
global uniform sampler2D i76_sw_ground;   // no source_color on a global: its texels come raw (sRGB)
global uniform bool i76_sw_ground_on;

#ifdef HORIZON_RING
#include "res://Shaders/horizon_mask.gdshaderinc"
#else
// the chunks ease onto the ring's own surface (UV2.x, where UV2.y is 1) between these distances from the camera, as the modern
// look's do (horizon_terrain.gdshaderinc), so the ring goes on where they end
uniform float morph_start = 800.0;
uniform float morph_end = 1200.0;
const float MORPH_LIFT = 2.5;
#endif

varying vec2 v_intensity;
varying vec3 g_pos;   // centre-relative, game frame

vec3 to_srgb(vec3 c) {
	return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
vec3 to_linear(vec3 c) {
	return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
#ifndef HORIZON_RING
	if (UV2.y > 0.5) {
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		VERTEX.y = mix(VERTEX.y, UV2.x + MORPH_LIFT, smoothstep(morph_start, morph_end, length(wp.xz - CAMERA_POSITION_WORLD.xz)));
	}
#endif
	g_pos = vec3(VERTEX.x, VERTEX.y, -VERTEX.z);
	// the gradient in the game's frame, as the 1997 terrain's UV2 carries it
	vec2 grad = vec2(-NORMAL.x, NORMAL.z) / max(NORMAL.y, 0.05);
	float d = light_game.y - grad.x * light_game.x - grad.y * light_game.z;
	float view_z = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
	float light = ambient;
	if (d >= 0.0) light = min(d * clamp(view_z * fade_a + fade_b, 0.75, 1.0) + ambient, 1.0);
	v_intensity = i76_ramp(light);
	UV = (g_pos.xz + centre) / 5.0;   // sample coordinates
}

void fragment() {
#ifdef HORIZON_RING
	// every path writes DEPTH (horizon_terrain.gdshaderinc: one that writes it on one path only leaves it undefined on the others)
	DEPTH = FRAGCOORD.z;
	int mask = horizon_mask(g_pos);
	if (mask == 1) discard;
	if (mask == 2) DEPTH = FRAGCOORD.z * 0.5;
#endif
	vec3 texel = show_light_only ? vec3(1.0)
		: i76_software ? (i76_sw_ground_on ? to_linear(i76_point(i76_sw_ground, UV / samples_per_texture).rgb) : i76_tex(i76_point(ground_tex, UV / samples_per_texture).rgb))
		: i76_tex(texture(ground_tex, UV / samples_per_texture).rgb);
	vec2 shade = v_intensity;
	if (i76_software) {
		// the Software renderer lights each face once, from its own normal (terrain_faithful.gdshader)
		vec3 n = normalize((INV_VIEW_MATRIX * vec4(cross(dFdx(VERTEX), dFdy(VERTEX)), 0.0)).xyz);
		if (n.y < 0.0) n = -n;
		float d = dot(vec3(n.x, n.y, -n.z), light_game);
		float fade = clamp(-VERTEX.z * fade_a + fade_b, 0.75, 1.0);
		shade = i76_ramp(d < 0.0 ? ambient : min(d * fade + ambient, 1.0));
	}
	ALBEDO = i76_out(to_linear(i76_shade(to_srgb(texel), shade)));
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) DIFFUSE_LIGHT += vec3(1.0);
}
