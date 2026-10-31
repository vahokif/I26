// Smacker movies, modern look, pass 1 (at the movie's own 640x480): palette-constrained debanding, conservative.
// Each pixel was snapped to the nearest colour of its frame's 256-entry palette, so its true colour lies in that
// entry's Voronoi cell. A band is a smooth gradient quantised into steps; detail is not. So: take the pixel's
// quantisation step s (the distance to the nearest different palette colour among its neighbours), and only where
// every neighbour within 3 pixels lies within a few steps (a smooth area, not an edge or texture) average the
// neighbours that are within about one step, then let the result move at most a third of a step from the pixel
// and never past the bisector to a neighbouring colour. Anything else is left exactly as the palette says.
shader_type canvas_item;

uniform sampler2D idx : filter_nearest, repeat_disable;   // R8: palette index per pixel
uniform sampler2D pal : filter_nearest, repeat_disable;   // 256x1 RGBA: this frame's palette
uniform sampler2D blk : filter_nearest, repeat_disable;   // R8, 160x120: how each 4x4 block was coded (0 skipped, 1 full, 2 two colours, 3 one colour)
uniform float block_edge = 0.10;   // a step across a block boundary bigger than this (RGB distance) is a real edge
uniform float deblock_strength = 1.0;   // I76_MOVIE_DEBLOCK=0 sets 0
uniform float reach = 0.35;     // how far (in steps) a pixel may move
uniform float smooth_to = 3.0;  // the neighbourhood counts as smooth if all taps are within this many steps

const int N = 16;
const vec2 TAPS[N] = vec2[N](
	vec2(1.000, 0.000),
	vec2(-1.000, 0.000),
	vec2(0.000, 1.000),
	vec2(0.000, -1.000),
	vec2(1.000, 1.000),
	vec2(-1.000, 1.000),
	vec2(1.000, -1.000),
	vec2(-1.000, -1.000),
	vec2(2.940, 0.596),
	vec2(1.658, 2.500),
	vec2(-0.596, 2.940),
	vec2(-2.500, 1.658),
	vec2(-2.940, -0.596),
	vec2(-1.658, -2.500),
	vec2(0.596, -2.940),
	vec2(2.500, -1.658)
);

vec3 col(vec2 uv) {
	float i = floor(texture(idx, uv).r * 255.0 + 0.5);
	return texture(pal, vec2((i + 0.5) / 256.0, 0.5)).rgb;
}


// how much a block of that code may be smoothed at its borders: a one- or two-colour block is a stand-in the encoder chose
// for something smoother; a fully coded block is the picture; a skipped one is stale, so nobody touches it
float blend_of(vec2 buv) {
	float t = floor(texture(blk, buv).r * 255.0 + 0.5);
	return t > 2.5 ? 1.0 : t > 1.5 ? 0.8 : t > 0.5 ? 0.15 : 0.0;
}

// the correction that blends this pixel toward the block next door across its nearer border in one direction
// (dir: (+-1, 0) or (0, +-1) to that border), over two pixels on each side, the way a loop filter does
vec3 deblock_dir(vec2 ipx, vec2 isz, vec2 dir, float o, vec2 buv, vec2 bsz) {
	// the block's edge pixel on this side, and the one across the border
	vec2 edge_px = ipx - dir * o;
	vec3 e = col((edge_px + 0.5) / isz);
	vec3 q = col((edge_px + dir + 0.5) / isz);
	float a = blend_of(buv), b = blend_of((floor(ipx / 4.0) + dir + 0.5) / bsz);
	float d = distance(e, q);
	float w = min(a, b) * (1.0 - smoothstep(0.5 * block_edge, block_edge, d));
	return (q - e) * (o < 0.5 ? 0.375 : 0.125) * w;
}

vec3 deblock(vec2 uv) {
	vec2 isz = vec2(textureSize(idx, 0)), bsz = vec2(textureSize(blk, 0));
	vec2 ipx = floor(uv * isz), m = mod(ipx, 4.0);
	vec2 buv = (floor(ipx / 4.0) + 0.5) / bsz;
	vec2 dir = vec2(m.x < 1.5 ? -1.0 : 1.0, 0.0), dirv = vec2(0.0, m.y < 1.5 ? -1.0 : 1.0);
	float ox = m.x < 1.5 ? m.x : 3.0 - m.x, oy = m.y < 1.5 ? m.y : 3.0 - m.y;
	return deblock_dir(ipx, isz, dir, ox, buv, bsz) + deblock_dir(ipx, isz, dirv, oy, buv, bsz);
}

void fragment() {
	vec2 px = 1.0 / vec2(textureSize(idx, 0));
	vec3 p = col(UV);
	vec3 c[N];
	float step_ = 1e9, far_ = 0.0;
	for (int k = 0; k < N; k++) {
		c[k] = col(UV + TAPS[k] * px);
		float d = distance(c[k], p);
		if (d > 1e-4) step_ = min(step_, d);
		far_ = max(far_, d);
	}
	COLOR = vec4(p, 1.0);
	if (step_ < 1e8 && far_ <= smooth_to * step_) {
	vec3 sum = p; float wsum = 1.0;
	for (int k = 0; k < N; k++) {
		float d = distance(c[k], p);
		float w = (k < 8 ? 1.0 : 0.6) * (1.0 - smoothstep(1.0, 1.6, d / step_));
		sum += c[k] * w; wsum += w;
	}
	vec3 res = sum / wsum;
	// not past the bisector to any neighbouring colour
	for (int it = 0; it < 2; it++) {
		for (int k = 0; k < N; k++) {
			vec3 n = c[k] - p;
			float nn = dot(n, n);
			float s = dot(res - p, n) - 0.5 * nn;
			if (nn > 1e-8 && s > 0.0) res -= n * (s / nn);
		}
	}
	vec3 m = res - p;
	float ml = length(m), lim = reach * step_;
	if (ml > lim) m *= lim / ml;
	COLOR = vec4(p + m, 1.0);
	}
	COLOR.rgb += deblock(UV) * deblock_strength;
}
