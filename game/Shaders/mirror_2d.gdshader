// The rear-view mirror on the 2D dash (slot 7): the same face as the 3D cockpit's
// (cockpit_mirror.gdshader), the view behind shown through the transparent pixels of the mirror's own art, here as the whole
// art, 256x64, bezel and all (the art's rows are stored bottom-up).
shader_type canvas_item;

uniform sampler2D art : source_color, filter_nearest;
uniform sampler2D view : source_color, filter_linear;
uniform sampler2D view_nearest : source_color, filter_nearest;
uniform bool faithful = false;
uniform vec2 clip = vec2(256.0, 64.0);
uniform vec2 art_size = vec2(256.0, 64.0);

void fragment() {
	vec2 auv = vec2(UV.x * clip.x / art_size.x, 1.0 - UV.y * clip.y / art_size.y);
	vec4 a = texture(art, auv);
	vec4 surround = texelFetch(art, ivec2(0, 0), 0);
	// within 0.1 of it: a pack's upscale (and its block compression) leaves the surround near, not exactly, the
	// original's colour; the art's other colours are a third of the way across the cube or more
	if (a.a >= 0.5 && distance(a.rgb, surround.rgb) < 0.1) discard;
	vec2 vuv = vec2(1.0 - auv.x, 1.0 - auv.y);
	vec3 v = faithful ? texture(view_nearest, vuv).rgb : texture(view, vuv).rgb;
	COLOR = vec4(a.a >= 0.5 ? a.rgb : v, 1.0);
}
