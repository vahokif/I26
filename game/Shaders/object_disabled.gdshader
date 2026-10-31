// Cut-out faces, drawn from both sides (object.gdshaderinc); modern look.
// The key is anti-aliased: alpha-to-coverage with an edge one texel wide, so a wheel's tyre and the
// like keep a smooth silhouette (a hard discard steps along the texels).
shader_type spatial;
render_mode cull_disabled, alpha_to_coverage;
#define KEY_COVERAGE
#include "res://Shaders/object.gdshaderinc"
