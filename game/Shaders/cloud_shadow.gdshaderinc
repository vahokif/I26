// The shadow a cloud casts on the ground, the cars and the bushes (docs/cloud-shadows.md).
// The modern sky's own clouds are the source: the same sheet, the same dome scale and the same scroll,
// so a shadow falls under the cloud that shows above it, and both drift together. Nothing is drawn and
// no light is cast: the covering is a number, which is what lets it reach the whole extended terrain
// and the distant horizon without a shadow cascade, and work the same on the Compatibility renderer.
//
// A point of the world is slid up the sun onto a plane at the height of the sky's dome, and the cloud
// covering that spot takes the sun's direct light out of the surface. The sky's fill stays: a host
// scales ALBEDO, which the engine multiplies into the ambient as well, and gives the fill back through
// EMISSION, so a shaded surface goes to cool sky light rather than to black.
//
// Only the modern look calls it. C# holds the strength, and it is 0 in the 1997 look, which shadows
// nothing but its own props, and in anything that never asked for a sky (menus, the editor, a garage).
#ifndef I76_CLOUD_SHADOW
#define I76_CLOUD_SHADOW
#include "res://Shaders/compat.gdshaderinc"

global uniform float i76_cloud_shadow = 0.0;   // how much of the sun a thick cloud takes: 0 (the default) is off
global uniform vec3 i76_cloud_sun = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the sun or moon, as i76_light_dir
global uniform vec3 i76_cloud_fill = vec3(0.0);   // what the sky gives a surface per unit albedo with the sun out
global uniform vec3 i76_cloud_anchor = vec3(0.0);   // Coords.ToGodot of the origin Rebase moved to the centre
global uniform vec2 i76_cloud_dome = vec2(6000.0, 5400.0);   // the sky's radius and drop, so the pattern is its pattern
global uniform float i76_cloud_scroll = 0.02997;   // and its scroll per second, so the two drift together
global uniform float i76_cloud_speed = 1.0;   // Graphic Detail's Cloud Speed: the sky multiplies its own scroll by it too
global uniform vec2 i76_cloud_span = vec2(0.15, 0.8);   // the sheet's open-sky and cloud levels (MissionBuilder.SkyPalette)
global uniform sampler2D i76_cloud_tex : source_color, filter_linear, repeat_enable;

// how much cloud hangs over a dome coordinate: the sky's density() without its detail noise, which at
// the ground's scale would be a second, faster pattern the eye reads as dapples crawling over the road
float i76_cloud_cover(vec2 uv) {
	// one fetch a mip down: the sheet's texels are tens of metres of cloud, and a shadow's edge is soft.
	// i76_tex, because the sheet is made at run time and Compatibility would hand it over undecoded, which
	// would read as cloud where the sky shows open blue (docs/compatibility-renderer.md).
	float luma = dot(i76_tex(textureLod(i76_cloud_tex, uv, 1.0)).rgb, vec3(0.299, 0.587, 0.114));
	float span = i76_cloud_span.y - i76_cloud_span.x;
	return smoothstep(i76_cloud_span.x - span * 0.1, i76_cloud_span.y + span * 0.15, luma);
}

// how much of the sun reaches a point of the world (Godot frame): 1 in the open, 1 - i76_cloud_shadow
// under the thickest cloud
float i76_cloud_shade(vec3 world) {
	if (i76_cloud_shadow <= 0.0) {
		return 1.0;
	}
	// as the sun goes low the plane it is projected onto runs away to nothing, and a sunset's shadow
	// across a whole map is long streaks rather than the shapes the sky shows
	float strength = i76_cloud_shadow * smoothstep(0.08, 0.3, i76_cloud_sun.y);
	if (strength <= 0.0) {
		return 1.0;
	}
	vec3 g = world + i76_cloud_anchor;   // the map's own coordinates, before Rebase moved its origin
	// up the sun to the dome's height: the apex of a dome of radius r dropped by d is r - d above ground
	float k = ((i76_cloud_dome.x - i76_cloud_dome.y) - g.y) / max(i76_cloud_sun.y, 0.15);
	// the sky samples its sheet in the game frame, which mirrors Godot's Z, and TIME is the sky's own
	vec2 q = (g.xz + i76_cloud_sun.xz * k) * vec2(1.0, -1.0);
	vec2 uv = (q / i76_cloud_dome.x + 0.5) * 4.0 + vec2(TIME * i76_cloud_scroll * i76_cloud_speed);
	return 1.0 - strength * i76_cloud_cover(uv);
}

// what the sky gives back where the cloud took the sun: the host adds it to EMISSION, times the albedo,
// before scaling ALBEDO by the shade. Metals take their fill from reflections, not from this, and the
// engine gives no ambient to a metal at all, so they get none back.
vec3 i76_cloud_fill_back(float shade, float metallic) {
	return i76_cloud_fill * (1.0 - shade) * (1.0 - metallic);
}
#endif
