// Opaque faces, drawn from both sides (object.gdshaderinc); modern look. The open wreck's shell and its thrown pieces.
// No alpha-to-coverage, unlike object_disabled: a shader that writes ALPHA that way is drawn after the opaque
// faces, in an order that is not depth's, so near and far faces of one mesh overdrew each other as the view moved.
shader_type spatial;
render_mode cull_disabled;
#include "res://Shaders/object.gdshaderinc"
