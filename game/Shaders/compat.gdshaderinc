// What the Compatibility renderer (OpenGL, the fallback for a GPU without Vulkan or D3D12: docs/compatibility-renderer.md)
// does differently from Forward+, as far as the colour of a shader's output goes:
//
// - It does not sRGB-decode a texture made at run time (an ImageTexture: the ground, the roads, the cars' paint, the
//   plants' atlas) on read, as its `source_color` hint asks. i76_tex asks for it by hand there, and is the colour as
//   it is elsewhere.
// - The 1997 look's frame (Linear tonemap) is not sRGB-encoded on its way to the screen, so everything there came out
//   dark and the lot of M15 black. The 1997 shaders put their colour through i76_out, which encodes it there.
// - Its depth buffer is not reversed and its clip space runs -1..1 in z, so a depth texture read (soft particles, the
//   fog pass) goes back to clip space as depth * 2 - 1 there, not as the raw depth. i76_ndc_depth does that.
#ifndef I76_COMPAT_INC
#define I76_COMPAT_INC
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
vec3 i76_out(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 i76_tex(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }
vec4 i76_tex(vec4 c) { return vec4(i76_tex(c.rgb), c.a); }
float i76_ndc_depth(float d) { return d * 2.0 - 1.0; }
#else
vec3 i76_out(vec3 c) { return c; }
vec3 i76_tex(vec3 c) { return c; }
vec4 i76_tex(vec4 c) { return c; }
float i76_ndc_depth(float d) { return d; }
#endif
#endif
