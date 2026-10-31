// Nitro's loading cards (addon\pNNload.pcx, loadscr.pcx) are white Impact on black at 640x480. In the modern look they are
// drawn at the window's resolution by thresholding a smooth (cubic B-spline) reconstruction of the picture at half
// brightness, with an edge one screen pixel wide: the letters come out round and sharp instead of blurred or blocky.
shader_type canvas_item;

vec4 cubic(float v) {
	vec4 n = vec4(1.0, 2.0, 3.0, 4.0) - v;
	vec4 s = n * n * n;
	float x = s.x, y = s.y - 4.0 * s.x, z = s.z - 4.0 * s.y + 6.0 * s.x;
	return vec4(x, y, z, 6.0 - x - y - z) / 6.0;
}

// the B-spline from four bilinear taps
vec4 bspline(sampler2D t, vec2 uv) {
	vec2 size = vec2(textureSize(t, 0)), inv = 1.0 / size;
	uv = uv * size - 0.5;
	vec2 f = fract(uv);
	uv -= f;
	vec4 xc = cubic(f.x), yc = cubic(f.y);
	vec4 c = uv.xxyy + vec2(-0.5, 1.5).xyxy;
	vec4 s = vec4(xc.xz + xc.yw, yc.xz + yc.yw);
	vec4 o = (c + vec4(xc.yw, yc.yw) / s) * inv.xxyy;
	vec4 a = texture(t, o.xz), b = texture(t, o.yz), cc = texture(t, o.xw), d = texture(t, o.yw);
	float sx = s.x / (s.x + s.y), sy = s.z / (s.z + s.w);
	return mix(mix(d, cc, sx), mix(b, a, sx), sy);
}

void fragment() {
	vec4 c = bspline(TEXTURE, UV);
	float l = max(c.r, max(c.g, c.b));
	float w = max(fwidth(l), 1e-4);
	float a = smoothstep(0.42 - w, 0.42 + w, l);
	vec3 ink = c.rgb / max(l, 1e-3);   // the letters' own colour at full brightness
	COLOR = vec4(ink * a, 1.0);
}
