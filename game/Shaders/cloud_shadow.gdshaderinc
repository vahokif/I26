// The shadow a cloud casts on the ground, the cars and the bushes (docs/cloud-shadows.md).
// The modern sky's own clouds are the source: the same sheet, the same dome scale and the same scroll,
// so a shadow falls under the cloud that shows above it, and both drift together. Nothing is drawn and
// no light is cast: the covering is a number, which is what lets it reach the whole extended terrain without
// a shadow cascade, and work the same on the Compatibility renderer.
//
// A point of the world is slid up the sun onto the sky's own sheet, and the cloud covering that spot takes
// the sun's direct light out of the surface. The sky's fill stays: a host scales ALBEDO, which the engine
// multiplies into the ambient as well, and gives the fill back through EMISSION, so a shaded surface goes to
// cool sky light rather than to black.
//
// The sheet is where the sky puts it, over the eye: a sphere of the sky's radius whose centre is the sky's
// drop below. The pattern is the map's, so a shadow stays on the ground it fell on as the trip covers a tile.
//
// Only the modern look calls it. C# holds the strength, and it is 0 in the 1997 look, which shadows
// nothing but its own props, and in anything that never asked for a sky (menus, the editor, a garage).
#ifndef I76_CLOUD_SHADOW
#define I76_CLOUD_SHADOW
#include "res://Shaders/compat.gdshaderinc"

global uniform float i76_cloud_shadow = 0.0;   // how much of the sun a thick cloud takes: 0 (the default) is off
global uniform vec3 i76_cloud_sun = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the sun or moon, as i76_light_dir
global uniform vec3 i76_cloud_fill = vec3(0.0);   // what the sky gives a surface per unit albedo with the sun out
global uniform vec3 i76_cloud_anchor = vec3(0.0);   // Coords.ToGodot of the origin Rebase moved: the pattern's, not the dome's
global uniform vec2 i76_cloud_dome = vec2(6000.0, 5400.0);   // the sky's radius and drop, so the pattern is its pattern
global uniform float i76_cloud_scroll = 0.02997;   // and its scroll per second, so the two drift together
global uniform float i76_cloud_speed = 1.0;   // Graphic Detail's Cloud Speed: the sky multiplies its own scroll by it too
global uniform vec2 i76_cloud_span = vec2(0.15, 0.8);   // the sheet's open-sky and cloud levels (MissionBuilder.SkyPalette)
global uniform float i76_cloud_shine = 0.6;   // how much of a paint's mirror the thickest cloud takes
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

// how much of the sun reaches a point of the world (Godot frame, so the eye is at the origin): 1 in the open
// and 1 wherever the sky's sheet is not overhead, 1 - i76_cloud_shadow under the thickest cloud
float i76_cloud_shade(vec3 world) {
	if (i76_cloud_shadow <= 0.0) {
		return 1.0;
	}
	// as the sun goes low, its shadow lands a map away from the cloud that cast it and that far out the sky
	// has already faded its own clouds into the horizon: better no shade than a streak no cloud accounts for
	float strength = i76_cloud_shadow * smoothstep(0.08, 0.3, i76_cloud_sun.y);
	if (strength <= 0.0) {
		return 1.0;
	}
	vec3 o = world + vec3(0.0, i76_cloud_dome.y, 0.0);   // from the sheet's centre, which is below the eye
	float c = dot(o, o) - i76_cloud_dome.x * i76_cloud_dome.x;
	// Outside that sphere there is no sheet at all: the sky's dome is 6000 of radius with its crown 600 over
	// the eye, so it stops being over the ground about 2600 out, and everything further than that shows the sky
	// behind it rather than cloud above it. Shading out there anyway - which the old projection onto the crown's
	// height did across the whole map, and over the 3D horizon's 1 to 3 km peaks, where the projection also ran
	// backwards - is what read as cloud rendered through the distant mountains.
	if (c >= 0.0) {
		return 1.0;   // the sun here comes from a part of the sky the sheet is not over
	}
	float b = dot(o, i76_cloud_sun);
	float k = -b + sqrt(b * b - c);   // the far crossing, the sheet above; c < 0 keeps the root real
	vec3 hit = world + i76_cloud_sun * k;
	// and only for the part of the sheet the sky shows: below a few degrees it fades its own clouds to the
	// horizon colour, so a shade down there would come from a cloud nobody can see. The rim of the dome is
	// where the crossing runs into the horizon, so this is also what ends the shade rather than a hard cut.
	strength *= smoothstep(0.0, 0.12, hit.y / max(length(hit), 1.0));
	if (strength <= 0.0) {
		return 1.0;
	}
	// the sky samples its sheet in the game frame, which mirrors Godot's Z, and TIME is the sky's own. The
	// anchor goes on at this end only, so the cloud pattern is the map's while the dome remains the eye's.
	vec2 q = (hit + i76_cloud_anchor).xz * vec2(1.0, -1.0);
	vec2 uv = (q / i76_cloud_dome.x + 0.5) * 4.0 + vec2(TIME * i76_cloud_scroll * i76_cloud_speed);
	return 1.0 - strength * i76_cloud_cover(uv);
}

// what the sky gives back where the cloud took the sun: the host adds it to EMISSION, times the albedo,
// before scaling ALBEDO by the shade. Metals take their fill from reflections, not from this, and the
// engine gives no ambient to a metal at all, so they get none back.
vec3 i76_cloud_fill_back(float shade, float metallic) {
	return i76_cloud_fill * (1.0 - shade) * (1.0 - metallic);
}

// how much of a gloss surface's mirror a cloud leaves, for a host to put on SPECULAR and CLEARCOAT.
// Scaling ALBEDO takes the sun out of everything the albedo feeds, but a car's hull is mostly reflection,
// and the engine has no per-pixel hold on the sun's specular: it is one light, and its own multipliers are
// per light, not per shade. So a gloss hull stays mirror-bright on a desert the sheet has gone dark over,
// which is the one place the pasted-on look the whole feature exists to stop comes straight back. What is
// taken is a share of the shine and not all of it, because the same f0 that catches the sun catches the
// sky, and a cloud overhead is not the sun going out. Only the hosts that are painted want this: the
// terrain and the road are rough enough that they show the shade through the albedo alone.
float i76_cloud_polish(float shade) {
	// the square root is a bias towards the thin end of the sheet: a haze takes the sun's glint off a
	// car well before it takes the light out of the ground, because the glint is one direction and the
	// first wisp is already in it. Linear, a cloud that visibly greys the desert leaves a hull mirror-bright.
	return 1.0 - i76_cloud_shine * sqrt(1.0 - shade);
}
#endif
