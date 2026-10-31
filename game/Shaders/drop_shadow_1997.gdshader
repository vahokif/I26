// The 1997 look's drop shadow (one for each object
// within 50 units of the camera): the caster's faces that
// turn toward the light, pushed along the light onto a plane laid on the ground
// under it. The plane is the instance's; i76_light_dir points toward the light. A big static caster (a bridge, a ramp) carries
// in CUSTOM0 each vertex's own point on the terrain along the light and its height above it (DropShadows.Draped),
// where one plane through its centre would stand up a canyon wall or hang in the air.
//
// Drawn as the software renderer darkened it, in either renderer (the 3D card filled the faces black, docs/deviations.md):
// mode 0xc0 remaps each pixel under the faces through a table built from the palette (the 1997 CD build's and the GOG
// build's): its HSV value v becomes v - 0.25 up to v = 0.6, 0.35 above, at least 0, hue and saturation kept
// (SoftwarePalette.Shadow). The opaque frame is read back, taken to the palette's own terms (the 1997 shaders write
// palette colours as sRGB), darkened and written; the Software renderer's final pass then matches it to the palette,
// as the table did. Every face reads the frame as it was before any shadow, so where faces overlap a pixel is darkened
// once (the original remapped it again for each face).
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

#include "res://Shaders/compat.gdshaderinc"

global uniform vec3 i76_light_dir;
uniform sampler2D screen : hint_screen_texture, filter_nearest;
instance uniform vec4 plane = vec4(0.0, 1.0, 0.0, 0.0);   // n·p = w, in world space
instance uniform vec4 caster_light = vec4(0.0);           // w = 1: toward this light instead (a car's light at night)

instance uniform float use_ground = 0.0;                 // 1: CUSTOM0 is each vertex's own shadow point on the terrain (xyz) and its height above it (w)

varying float v_facing;
varying float v_clip;

vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 to_linear(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }

void vertex() {
	vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 l = normalize(caster_light.w > 0.5 ? caster_light.xyz : i76_light_dir);
	v_facing = dot(n, l);
	float t = (dot(plane.xyz, w) - plane.w) / max(dot(plane.xyz, l), 0.05);
	// a few centimetres off the ground, so the terrain under it does not cut it up
	vec3 p = (use_ground > 0.5 ? CUSTOM0.xyz : w - l * t) + plane.xyz * 0.06;
	v_clip = use_ground > 0.5 ? CUSTOM0.w : 1.0;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(p, 1.0);
}

void fragment() {
	if (v_facing < 0.0) discard;
	if (v_clip < 0.05) discard;   // what lies under the ground casts nothing
	// the frame in palette terms (Compatibility's 1997 frame already holds them: i76_tex undoes its encode)
	vec3 dst = to_srgb(clamp(i76_tex(textureLod(screen, SCREEN_UV, 0.0).rgb), 0.0, 1.0));
	float v = max(dst.r, max(dst.g, dst.b));
	float sv = max(v > 0.6 ? 0.35 : v - 0.25, 0.0);
	ALBEDO = i76_out(to_linear(v > 1e-4 ? dst * (sv / v) : vec3(0.0)));
	ALPHA = 1.0;   // drawn with the transparent ones, after the opaque frame it reads
}
