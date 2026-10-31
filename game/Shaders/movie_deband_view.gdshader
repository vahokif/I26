// Smacker movies, modern look, pass 2: the debanded 640x480 picture (16-bit float) drawn at the window's
// resolution through a Catmull-Rom reconstruction, with a whisper of dither to hide the display's own 8-bit steps.
shader_type canvas_item;

// Catmull-Rom (interpolating, so it keeps the picture's sharpness) from nine bilinear taps
vec4 catmull(sampler2D t, vec2 uv) {
	vec2 size = vec2(textureSize(t, 0));
	vec2 pos = uv * size, c = floor(pos - 0.5) + 0.5, f = pos - c;
	vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
	vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
	vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
	vec2 w3 = f * f * (-0.5 + 0.5 * f);
	vec2 w12 = w1 + w2, o12 = w2 / w12;
	vec2 t0 = (c - 1.0) / size, t3 = (c + 2.0) / size, t12 = (c + o12) / size;
	vec4 r = texture(t, vec2(t0.x, t0.y)) * w0.x * w0.y + texture(t, vec2(t12.x, t0.y)) * w12.x * w0.y + texture(t, vec2(t3.x, t0.y)) * w3.x * w0.y
		+ texture(t, vec2(t0.x, t12.y)) * w0.x * w12.y + texture(t, vec2(t12.x, t12.y)) * w12.x * w12.y + texture(t, vec2(t3.x, t12.y)) * w3.x * w12.y
		+ texture(t, vec2(t0.x, t3.y)) * w0.x * w3.y + texture(t, vec2(t12.x, t3.y)) * w12.x * w3.y + texture(t, vec2(t3.x, t3.y)) * w3.x * w3.y;
	return r;
}

void fragment() {
	vec3 c = max(catmull(TEXTURE, UV), vec4(0.0)).rgb;
	// interleaved gradient noise, triangular, +-1 LSB at most
	float a = fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
	float b = fract(52.9829189 * fract(dot(FRAGCOORD.xy + 17.0, vec2(0.06711056, 0.00583715))));
	c += (a + b - 1.0) / 255.0;
	COLOR = vec4(c, 1.0);
}
