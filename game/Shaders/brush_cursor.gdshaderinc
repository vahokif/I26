// The map editor's brush cursor, drawn into the terrain's own shaders so it lies on the ground exactly, in both looks.
// i76_brush: x, z (game metres), radius (metres), on. i76_brush_style: the tool's colour, and the falloff (0 cosine, 1 linear,
// 2 sharp, 3 plateau). Nothing draws while the editor is closed (on = 0).
global uniform vec4 i76_brush;
global uniform vec4 i76_brush_style;
// The Surface tool's overlay: a window of the surface map, one texel per sample, rgb the type's colour, a the strength.
// i76_surface_rect: x, z of the window's corner (game metres), 1 / its size (metres). i76_surface_on.x: 1 while shown.
global uniform sampler2D i76_surface : filter_nearest, repeat_disable;
global uniform vec4 i76_surface_rect;
global uniform vec4 i76_surface_on;

vec4 surface_overlay(vec2 p) {
	if (i76_surface_on.x < 0.5) return vec4(0.0);
	vec2 uv = (p - i76_surface_rect.xy) * i76_surface_rect.zw;
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) return vec4(0.0);
	return texture(i76_surface, uv);
}

float brush_weight(float t, float kind) {
	t = clamp(t, 0.0, 1.0);
	if (kind < 0.5) return 0.5 * (1.0 + cos(3.14159265 * t));
	if (kind < 1.5) return 1.0 - t;
	if (kind < 2.5) return (1.0 - t) * (1.0 - t);
	return t < 0.5 ? 1.0 : 0.5 * (1.0 + cos(3.14159265 * (t - 0.5) * 2.0));
}

// returns (ring, disc): the rim's line and the falloff's faint fill, 0..1
vec2 brush_cursor(vec2 p) {
	if (i76_brush.w < 0.5) return vec2(0.0);
	float r = i76_brush.z;
	float d = length(p - i76_brush.xy);
	float fw = max(fwidth(d), 1e-4);
	float width = max(fw * 1.5, r * 0.012);
	float ring = 1.0 - smoothstep(width * 0.5, width, abs(d - r));
	float core = (1.0 - smoothstep(width * 0.3, width * 0.6, abs(d - r * 0.5))) * 0.35;
	float disc = d < r ? brush_weight(d / r, i76_brush_style.w) : 0.0;
	return vec2(max(ring, core), disc);
}
