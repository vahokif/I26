// Translucent faces (GEO face +0x21 != 0: lamp cones, rotor blades, explosions), keyed and
// blended at 0x99/255, both sides (object.gdshaderinc); modern look.
shader_type spatial;
render_mode cull_disabled, blend_mix, depth_draw_opaque;
#define TRANSLUCENT
#include "res://Shaders/object.gdshaderinc"
