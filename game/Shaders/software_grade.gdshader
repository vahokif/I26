// The Software renderer's last step, and the modern look's palette grade (SoftwareLook.cs): every pixel becomes
// its nearest colour in the mission's 8-bit palette, as the original's software frame is nothing but palette
// indices. One fetch of the frame and one of a 64³ lookup (SoftwarePalette.BuildLut,
// 8×8 tiles of 64×64: blue picks the tile, red and green the texel).
//
// lines > 0: the frame point-sampled on a grid that many pixels tall (480: the software renderer's 640x480),
// square cells, as an integer-free nearest upscale would show it.
// gamma: Monitor Brightness, applied to the palette colour after the match, as the original re-ramps its
// palette rather than the picture: level 3 is 1, the identity.
// The frame comes in sRGB-encoded (no HDR 2D), the palette's own terms.
shader_type canvas_item;

uniform sampler2D screen : hint_screen_texture, filter_nearest;
uniform sampler2D lut : filter_nearest, repeat_disable;
uniform float lines = 0.0;
uniform float gamma = 1.0;
uniform float strength = 1.0;   // 1: the palette colour; less blends back toward the frame (the modern grade)
uniform float dither = 0.0;     // the modern grade only: a 4x4 ordered dither of this amplitude before the match (the original never dithered)

const float N = 64.0;

vec3 nearest(vec3 c) {
	vec3 i = clamp(floor(c * (N - 1.0) + 0.5), 0.0, N - 1.0);
	vec2 tile = vec2(mod(i.b, 8.0), floor(i.b / 8.0));
	ivec2 at = ivec2(tile * N + i.rg);
	return texelFetch(lut, at, 0).rgb;
}

void fragment() {
	vec2 frag = FRAGCOORD.xy;
	if (lines > 0.0) {
		float cell = 1.0 / SCREEN_PIXEL_SIZE.y / lines;
		frag = (floor(frag / cell) + 0.5) * cell;
	}
	vec3 c = textureLod(screen, frag * SCREEN_PIXEL_SIZE, 0.0).rgb;
	if (dither > 0.0) {
		ivec2 q = ivec2(frag) & ivec2(3);
		const float bayer[16] = float[](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
		c += (bayer[q.y * 4 + q.x] / 16.0 - 0.46875) * dither;
	}
	vec3 p = nearest(c);
	COLOR = vec4(pow(mix(c, p, strength), vec3(1.0 / gamma)), 1.0);
}
