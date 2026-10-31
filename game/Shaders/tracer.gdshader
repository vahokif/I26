// A round in flight as a glowing streak: a quad from tail to head, turned to
// face the camera about its own axis, with a white-hot core, a broad coloured
// halo that stands in for bloom, a brighter head and a tail that fades in.
// Additive; `width` is the halo's half-width.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

instance uniform vec3 tail;
instance uniform vec3 head;
instance uniform vec4 tint : source_color = vec4(1.0, 0.75, 0.35, 1.0);
instance uniform float width = 0.08;
instance uniform float energy = 6.0;

varying float across;

void vertex() {
	vec3 axis = head - tail;
	float len = length(axis);
	vec3 a = len > 1e-4 ? axis / len : vec3(0.0, 0.0, 1.0);
	vec3 p = mix(tail, head, UV.x);
	vec3 to_cam = CAMERA_POSITION_WORLD - p;
	float dist = length(to_cam);
	vec3 side = cross(a, to_cam / max(dist, 1e-4));
	float sl = length(side);
	side = sl > 1e-4 ? side / sl : vec3(0.0, 1.0, 0.0);
	// at least ~1.5 px wide however far away, so distant fire still reads
	float w = max(width, dist * 0.0025);
	across = UV.y * 2.0 - 1.0;
	p += side * across * w;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(p, 1.0);
}

void fragment() {
	float v = across;
	float core = exp(-v * v * 45.0);
	float halo = exp(-v * v * 5.0) * 0.45;
	float along = smoothstep(0.0, 0.9, UV.x);
	float headglow = (1.0 + 1.5 * smoothstep(0.8, 1.0, UV.x)) * (1.0 - smoothstep(0.95, 1.0, UV.x));   // bright to the head, then a soft end
	vec3 hot = vec3(1.0, 0.97, 0.88) * core * 1.6;
	ALBEDO = (hot + tint.rgb * halo) * energy * along * along * headglow;
}
