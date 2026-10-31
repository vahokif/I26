// The 1997 look's Software renderer (SoftwareLook.cs): the original's own
// 8-bit rasteriser point-samples its textures (every textured span routine reads one texel, no blend), and its
// light goes through the palette's .lum rows rather than a grey multiply. the ramp (ramp.gdshaderinc) then holds, per light level,
// the row's fit colour·a + b (SoftwarePalette.Ramp) in r and g; the 3D card's ramp is r alone (g reads 0).
#include "res://Shaders/compat.gdshaderinc"
global uniform bool i76_software;



// a texel, point-sampled from the mip level the footprint asks for (rounded down: the software renderer's
// ground picks its level per leaf by depth, one per halving). A macro:
// Godot refuses one function called with samplers of different hints.
int i76_point_level(vec2 uv, ivec2 size, int levels) {
	vec2 t = uv * vec2(size);
	vec2 dx = dFdx(t), dy = dFdy(t);
	float lod = max(0.0, 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8)));
	return min(int(lod), levels - 1);
}
ivec2 i76_point_px(vec2 uv, ivec2 size, int level) {
	ivec2 sz = max(size >> ivec2(level), ivec2(1));
	ivec2 p = ivec2(floor(uv * vec2(sz)));
	return ((p % sz) + sz) % sz;
}
// the mip count: textureQueryLevels is high-end only (Forward+/Mobile); the Compatibility renderer (OpenGL, for a GPU
// without Vulkan/D3D12) lacks it, so there the full chain's length comes from the size (every mipmapped texture here has one)
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
#define I76_MIPS(S) (int(floor(log2(float(max(textureSize(S, 0).x, textureSize(S, 0).y))))) + 1)
#else
#define I76_MIPS(S) textureQueryLevels(S)
#endif
#define I76_LEVEL(S, UV_) i76_point_level(UV_, textureSize(S, 0), I76_MIPS(S))
#define i76_point(S, UV_) texelFetch(S, i76_point_px(UV_, textureSize(S, 0), I76_LEVEL(S, UV_)), I76_LEVEL(S, UV_))

// a shade (a, b) applied to an sRGB colour
vec3 i76_shade(vec3 srgb, vec2 ab) { return srgb * ab.x + vec3(ab.y); }
