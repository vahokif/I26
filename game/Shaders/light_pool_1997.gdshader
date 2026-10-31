// The night's headlight pool in the 1997 look: hdlt_msk.geo, a flat fan 20 units long and 15.5 wide in front of the
// car, drawn in mode 0xc4, which renders it twice as primitive 5. ZGLIDE's Render
// draws primitive 5 untextured in a constant white (grConstantColorValue(0xffffffff), grColorCombine(1,0,1,2,0); the
// face's HEADLT.MAP never reaches the screen) blended DST_COLOR / ONE, so each pass is dst + dst: what lies under the
// fan comes out at four times its brightness, clamped, with hard edges (measured on the real plugin under
// OpenGLide: the M04 ground 13,13,19 -> 52,52,76 inside the fan). In the palette's own terms: the opaque frame is
// read back, taken to those terms (the 1997 shaders write palette colours as sRGB), scaled and written.
//
// The original paints it over the terrain and the road pieces in its draw order; here it is depth-tested, pulled a
// little toward the camera so the ground's small bumps under the fan do not cut it.
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D screen : hint_screen_texture, filter_nearest;
uniform float pull = 0.6;

vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 to_linear(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }

void vertex() {
	vec3 v = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float d = length(v);
	v *= max(d - pull, 0.05) / max(d, 0.05);
	POSITION = PROJECTION_MATRIX * vec4(v, 1.0);
}

void fragment() {
	vec3 dst = to_srgb(clamp(textureLod(screen, SCREEN_UV, 0.0).rgb, 0.0, 1.0));
	ALBEDO = to_linear(min(dst * 4.0, vec3(1.0)));   // two passes of dst * white + dst
	ALPHA = 1.0;   // drawn with the transparent ones, after the opaque frame it reads
}
