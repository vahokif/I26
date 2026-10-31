// Tyre skid marks (SkidMarks). Vertex colour: rgb the mark's colour, a its opacity; UV.x across the strip 0..1,
// UV.y metres along it. 1997: the original's flat dark polygon, hard-edged. Modern: soft edges, a broken, faintly
// treaded texture, lighter and dusty on dirt (the colour comes from the surface under each sample).
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, skip_vertex_transform;

uniform bool modern = true;
uniform float scene_light = 1.0;
instance uniform float fade = 1.0;

varying vec4 v_c;
varying vec2 v_uv;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}

void vertex() {
	// view space, pulled a hair toward the camera: depth bias without a visible lift, so the strip never
	// fights the terrain under it
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	VERTEX *= 0.996;
	v_c = COLOR;
	v_uv = UV;
}

void fragment() {
	float a = v_c.a * fade;
	if (modern) {
		float e = abs(v_uv.x * 2.0 - 1.0);
		a *= 1.0 - smoothstep(0.5, 1.0, e);
		float along = vnoise(vec2(v_uv.y * 2.2, v_uv.x * 3.0 + 11.0));
		a *= 0.78 + 0.35 * along;
		float tread = 0.86 + 0.14 * sin((v_uv.x * 7.0 + floor(v_uv.y * 5.0) * 0.37) * 6.2832);
		a *= tread;
	}
	if (a < 0.004) discard;
	ALBEDO = pow(v_c.rgb, vec3(2.2)) * scene_light;
	ALPHA = clamp(a, 0.0, 1.0);
}
