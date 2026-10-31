// Translucent faces, front only (object.gdshaderinc); 1997 look. The engine draws no face of a scene object from
// behind, translucent or not: MaterialLibrary.ForTexture's oneSided1997.
shader_type spatial;
render_mode cull_back, blend_mix, depth_draw_opaque;
#define TRANSLUCENT
#define FAITHFUL
#include "res://Shaders/object.gdshaderinc"
