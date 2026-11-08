// The engine's terrain light:
//
//   dot   = Ly - dhdx*Lx - dhdz*Lz        against the UNnormalised (-dhdx, 1, -dhdz)
//   dot < 0  -> ambient
//   fade  = clamp(viewZ*fade_a + fade_b, 0.75, 1)
//   light = min(dot*fade + ambient, 1)
//
// computed per vertex, as the engine does, and looked up in the mission's
// light ramp (LUMA[round(31·light)]: its floor well above
// black, saturating near 0.74) before it scales the texel; that value is
// what is interpolated across the face, as the plugin's per-vertex
// intensity is.
//
// Roads (both looks paint them into the terrain, RoadRaster): the layer is cut
// hard at half alpha, as the engine's strips keyed their texels, and lit with
// the engine's road light: max(0, I*Ly) + ambient, gamma-encoded, with no
// depth term (the ground's fade does not reach the roads).
//
// No shadow map in this look (the original had none; its drop shadows are
// projected polygons, DropShadows.cs): light() passes the engine's colour.
//
// The Software renderer (software.gdshaderinc, i76_software): the engine lights each face once, from its own
// normal (max(n·L, 0)·fade + ambient, the fade at its first vertex), where the
// 3D card gets a light per vertex; so here the face's normal from the position's screen derivatives, light per
// fragment (constant over a flat face), and the texel point-sampled.
//
// UV  = sample coordinates (for the ground texture).
// UV2 = (dhdx, dhdz) in sample units, in the game's own frame, as the engine
//       forms them; light_game is in the game's frame too.
shader_type spatial;
render_mode cull_back;

uniform sampler2D ground_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 light_game = vec3(0.9659, 0.2588, 0.0);
uniform float ambient = 0.0;
uniform float fade_a = 0.000417;
uniform float fade_b = 0.7496;
uniform float gamma_exp = 0.4;
uniform float samples_per_texture = 8.0;
uniform bool show_light_only = false;
uniform float shadow_dark = 0.0;   // the original's shadows were pure black
uniform sampler2D road_layer : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform bool has_road = false;
uniform float chunk_units = 160.0;
uniform float road_light_y = 0.2588;
// the outside ground's chunks (OutsideTerrain): cut at this view depth, the original's 600 m clip, whatever the draw distance (0: none)
uniform float clip_depth = 0.0;

// the engine's light ramp (the mission's .act and .lum): a vertex lit `light` takes LUMA[round(31·light)]
#include "res://Shaders/ramp.gdshaderinc"
#include "res://Shaders/software.gdshaderinc"
// the Software renderer's ground: the miss8 mission's family (tt…sd_, 128x128), when there is one
global uniform sampler2D i76_sw_ground;   // no source_color on a global: its texels come raw (sRGB)
global uniform bool i76_sw_ground_on;

#include "res://Shaders/brush_cursor.gdshaderinc"

varying vec2 v_intensity;
varying vec2 v_road;
varying vec2 road_uv;

vec3 to_srgb(vec3 c) {
	return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
vec3 to_linear(vec3 c) {
	return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c));
}

void vertex() {
	float d = light_game.y - UV2.x * light_game.x - UV2.y * light_game.z;
	float view_z = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
	float light;
	if (d < 0.0) {
		light = ambient;
	} else {
		float fade = clamp(view_z * fade_a + fade_b, 0.75, 1.0);
		light = min(d * fade + ambient, 1.0);
	}
	v_intensity = i76_ramp(light);
	v_road = i76_ramp(max(road_light_y, 0.0) + ambient);
	road_uv = vec2(VERTEX.x, -VERTEX.z) / chunk_units;
}

void fragment() {
	if (clip_depth > 0.0 && -VERTEX.z > clip_depth) discard;
	vec3 texel = show_light_only ? vec3(1.0)
		: i76_software ? (i76_sw_ground_on ? to_linear(i76_point(i76_sw_ground, UV / samples_per_texture).rgb) : i76_tex(i76_point(ground_tex, UV / samples_per_texture).rgb))
		: i76_tex(texture(ground_tex, UV / samples_per_texture).rgb);
	vec2 shade = v_intensity;
	if (i76_software) {
		// the face's normal, in the game's frame (Godot's with Z mirrored)
		vec3 n = normalize((INV_VIEW_MATRIX * vec4(cross(dFdx(VERTEX), dFdy(VERTEX)), 0.0)).xyz);
		if (n.y < 0.0) n = -n;
		float d = dot(vec3(n.x, n.y, -n.z), light_game);
		float fade = clamp(-VERTEX.z * fade_a + fade_b, 0.75, 1.0);
		shade = i76_ramp(d < 0.0 ? ambient : min(d * fade + ambient, 1.0));
	}
	ALBEDO = i76_out(to_linear(i76_shade(to_srgb(texel), shade)));
	if (has_road && !show_light_only) {
		// the 3D card's roads are bilinear with no mip chain: the layer's top level, however far or slanted
		vec4 r = i76_software ? i76_point(road_layer, road_uv) : textureLod(road_layer, road_uv, 0.0);
		r.rgb = i76_tex(r.rgb);
		if (r.a >= 0.5) ALBEDO = i76_out(to_linear(i76_shade(to_srgb(r.rgb), v_road)));
	}
	{
		vec4 so = surface_overlay(UV * 5.0);
		ALBEDO = mix(ALBEDO, so.rgb, so.a);
		vec2 bc = brush_cursor(UV * 5.0);
		ALBEDO = mix(ALBEDO, i76_brush_style.rgb, clamp(bc.x * 0.85 + bc.y * 0.22, 0.0, 1.0));
	}
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) DIFFUSE_LIGHT += vec3(1.0);
}
