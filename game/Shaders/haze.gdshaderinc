// The air over the desert: how much of it lies between the eye and a pixel, and what the sun adds to it
// (docs/desert-haze.md).
//
// The density falls off with height over a reference level (the mission's mean ground, pushed by DesertHaze.cs),
// so a ray down a valley gathers more air than the same ground seen from a peak, and a ray that skims the
// ground gathers more than one that climbs away from it. The exponential integrates exactly along the segment:
// with x the height the ray gains over its own length, in scale heights, the slant depth is the depth of the
// same length of level air at the eye's own height times (1 - exp(-x))/x, which is 1 along a level ray, under
// 1 climbing and over 1 descending. Nothing is sampled and no altitude is read: the ground a ray stops on is
// already in the depth buffer, and only its distance is used here.
//
// The light the air adds is the sky's own in that direction with its clouds and its disc out of it (the host
// calls modern_air for it: a cloud's shape cannot survive being scattered on the way, and stamping one onto
// distant ground is what makes a cloud look painted through the ridge in front of it) plus the sun's
// forward scattering: a Henyey-Greenstein lobe over the angle between the view and the sun, scaled so that
// looking into the sun is 1. That is what turns a flat horizon gold on a drive toward the evening light, and
// leaves the same horizon its plain sky colour looking away from it.
//
// DesertHaze.cs (game/Scripts/World) holds the numbers, the day and night shares, the look switch and the
// reference level, and pushes them as globals; they are 0 for the 1997 look and for anything that never asked
// for a sky. Its SlantDepth and Phase are this maths in C#, with the tests: change one and change the other.
#ifndef I76_HAZE
#define I76_HAZE

global uniform float i76_haze_density = 0.0;   // what a metre of the air at the reference level takes, 0 (the default) is no haze at all
global uniform float i76_haze_scale = 400.0;   // the height of the layer this takes to thin the air to a third of that, metres
global uniform float i76_haze_floor = 0.0;     // the reference level: the mission's mean ground, as a Godot Y
global uniform vec3 i76_haze_sun = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the sun or moon, as i76_light_dir has it
global uniform vec3 i76_haze_tint = vec3(0.0);   // what sunlit air adds, in the sky's own colours for that sun height
global uniform float i76_haze_mie = 0.0;   // how much of the tint the lobe adds: 0 leaves the haze the plain sky colour

const float HAZE_G = 0.6;         // the lobe's forward bias: dust scatters hard forward, and this is dust
const float HAZE_CLIMB = 1.2;     // how far below the reference level the law is trusted, in scale heights: past it a valley's air stops thickening

// what the air takes from the sun's own light, 1 straight into it and about a fortieth at right angles to it
float i76_haze_phase(float towards) {
	float g2 = HAZE_G * HAZE_G;
	float f = (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * HAZE_G * towards, 1e-4), 1.5);
	float straight = (1.0 - g2) / pow(1.0 + g2 - 2.0 * HAZE_G, 1.5);   // the same into the sun, so the lobe peaks at 1
	return f / straight;
}

// how much air lies along the ray: eye its Godot height, dir the unit direction, dist the metres to the surface it stopped on
float i76_haze_depth(float eye, vec3 dir, float dist) {
	if (i76_haze_density <= 0.0 || dist <= 0.0) return 0.0;
	float h = max(i76_haze_scale, 1.0);
	float x = dir.y * dist / h;   // the height the ray gains over its own length, in scale heights
	// (1 - exp(-x))/x, the exponential's integral along the segment; its series near a level ray, where the fraction is 0/0
	float spread = abs(x) < 1e-3 ? 1.0 - 0.5 * x + x * x / 6.0 : (1.0 - exp(-x)) / x;
	float at_eye = exp(-clamp((eye - i76_haze_floor) / h, -HAZE_CLIMB, 30.0));
	float tau = i76_haze_density * dist * at_eye * spread;
	if (!(tau > 0.0)) return 0.0;   // and never a NaN or a runaway: 8 is opaque and nothing beyond it is seen anyway
	return min(tau, 8.0);
}
#endif
