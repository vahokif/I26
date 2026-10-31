// Translucent faces, both sides (object.gdshaderinc); 1997 look.
shader_type spatial;
render_mode cull_disabled, blend_mix, depth_draw_opaque;
#define TRANSLUCENT
#define FAITHFUL
#include "res://Shaders/object.gdshaderinc"
