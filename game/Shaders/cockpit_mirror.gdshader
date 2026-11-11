// The rear-view mirror's face: the view behind, rendered by the
// mirror camera, shown through the transparent pixels of the mirror's own art
// (zmiri101), which is drawn over it keyed; the art's surround is see-through,
// as the original shows it. The view is mirrored left-right,
// and turned to the art's bottom-up row order. Pre-lit like the rest of the
// cockpit: no light reaches it.
shader_type spatial;
render_mode unshaded, cull_back;

uniform sampler2D art : source_color, filter_nearest;
uniform sampler2D view : source_color, filter_linear;
uniform sampler2D view_nearest : source_color, filter_nearest;
uniform bool faithful = false;
uniform bool hidden = false;

void fragment() {
	if (hidden) discard;
	vec4 a = texture(art, UV);
	// the art's surround (its corner colour) is not drawn: only the frame and the glass are
	vec4 surround = texelFetch(art, ivec2(0, 0), 0);
	// within 0.1 of it: a pack's upscale (and its block compression) leaves the surround near, not exactly, the
	// original's colour; the art's other colours are a third of the way across the cube or more
	if (a.a >= 0.5 && distance(a.rgb, surround.rgb) < 0.1) discard;
	vec2 vuv = vec2(1.0 - UV.x, 1.0 - UV.y);
	vec3 v = faithful ? texture(view_nearest, vuv).rgb : texture(view, vuv).rgb;
	ALBEDO = a.a >= 0.5 ? a.rgb : v;
}
