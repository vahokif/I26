// The modern sky: the mission's own sky texture as the cloud field, made
// volumetric-looking. Its brightness becomes cloud density (sky_lum ->
// cloud_lum, measured from the texture), projected the way the engine's dome
// projects it (radius 6000, centred 5400 below the eye, the
// texture repeating 4 times across it, scrolled), so the clouds sit where the
// original's do. Procedural noise breaks the edges into billows, a sample
// toward the sun lights the near edges and shades the undersides, a second
// fainter layer drifts slower and higher, and the sky behind is a gradient in
// the texture's own blue with a sun glow. The horizon panels are drawn as in
// sky_dome.gdshader; by day a drawn sun replaces the sprite, at night the moon
// sprite stays; either shows through the clouds only where they are thin.
shader_type sky;

#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
#define I76_RAW_SKY
#endif
#include "sky_modern.gdshaderinc"

void sky() {
	vec3 c = modern_sky(EYEDIR, TIME, AT_CUBEMAP_PASS);
#ifdef I76_RAW_SKY
	// the radiance cube map is the scene's ambient light, linear; the sky itself is on screen
	if (!AT_CUBEMAP_PASS) c = i76_linear_to_srgb(c);
#endif
	COLOR = c;
}
