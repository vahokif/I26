// The modern look's cluster bomb trail: a camera-facing ribbon along the bomb's last 23 stamped points.
// UV.x runs 0 at the bomb to 1 at the oldest end, UV.y across (0..1), UV2.x is the metres behind the bomb,
// COLOR.r a per-bomb seed. A flame core flickering at the head, cooling through orange and red to dark smoke
// along its length, the noise scrolling back from the bomb. Premultiplied like wreck_flame: the flame part
// is mostly added (it blooms, and glows at night), the smoke part laid over (it reads dark by day).
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

// the scene's light, 1 by day and lower at night: only the smoke follows it, the fire is its own light
uniform float daylight = 1.0;

float h21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float n2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	CAB_CLIP
	float s = UV.x;
	float v = UV.y * 2.0 - 1.0;
	float seed = COLOR.r * 41.0;
	float t = TIME;
	float m = UV2.x;
	float n = n2(vec2(m * 0.45 - t * 6.0 + seed, v * 1.8 + seed * 0.3)) * 0.6
	        + n2(vec2(m * 1.1 - t * 11.0 - seed, v * 4.0)) * 0.4;
	// the plume swells off the bomb, then thins out
	float width = mix(0.45, 1.0, smoothstep(0.0, 0.3, s)) * (1.0 - 0.45 * smoothstep(0.6, 1.0, s));
	float body = 1.0 - abs(v) / width;
	float f = smoothstep(0.05, 0.6, body * 1.15 + (n - 0.5) * 1.5 - s * 0.6);
	f *= smoothstep(0.0, 0.025, s) * (1.0 - smoothstep(0.8, 1.0, s));
	float flick = 0.85 + 0.3 * n2(vec2(t * 18.0, seed));
	float temp = clamp(f * (1.3 - s * 1.7) * flick + (n - 0.5) * 0.25, 0.0, 1.0);
	vec3 col = mix(vec3(0.5, 0.05, 0.0), vec3(1.0, 0.27, 0.02), smoothstep(0.1, 0.45, temp));
	col = mix(col, vec3(1.0, 0.6, 0.12), smoothstep(0.6, 1.0, temp) * (0.3 + 0.7 * n));
	// a bright flame core right behind the bomb
	col = mix(col, vec3(1.0, 0.85, 0.5), smoothstep(0.75, 1.0, temp) * (1.0 - smoothstep(0.0, 0.14, s)));
	col *= 0.4 + 0.5 * temp;
	// cooled to smoke toward the old end
	float smoke = smoothstep(0.22, 0.65, s);
	vec3 smoke_col = vec3(0.11, 0.095, 0.085) * (0.35 + 0.65 * daylight) * (0.7 + 0.5 * n);
	col = mix(col, smoke_col, smoke);
	float a = f * mix(1.0, 0.8, smoke);
	ALBEDO = col * a;
	ALPHA = a * mix(0.35, 0.95, smoke);
}
