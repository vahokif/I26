// The modern sky with volumetric clouds, the march and the composite (Graphic Detail's Clouds: Volumetric; the modern look on
// Forward+ and Mobile). The sky, sun, moon and haze are sky_modern's; the flat cloud layer is replaced by a layer of clouds
// fixed in the world (not centred on the eye as the original's dome is), 450 m deep and 1.5 km over the map's average
// ground, its floor curving down with the Earth's radius so distant clouds meet the horizon. The cover is the flat sky's own
// cloud density laid flat over the world (cover_map, CloudNoise.Cover: the mission's sky texture ramped as the flat sky
// ramps it, moved by the same lumps and frayed by the same fine noise), a repeat every cover_tile metres, carried by a wind
// as fast overhead as the dome's texture scrolls and in its direction, on the mission's clock (cloud_time), so every player sees the
// same clouds in the same place. Each cloud is as dense as lets through, looking straight up, what the flat sky's cover lets
// through, and as deep as it is dense: veils at the edges, lumpy cores whose roofs and floors the noise moves; tiling 3D
// noise (CloudNoise: Perlin-Worley lumps, Worley fibres), drawn out a little along the wind, the layer sheared downwind with height
// and the cover pushed about by a coarse noise, makes them streaky and frayed. They are lit as the flat sky lights its
// clouds, in the texture's measured colours, once a pixel at the point they are seen at (clouds_light): bright rims where
// the way toward the sun is clear, shaded where it finds cloud, a silver lining on the thin parts toward the sun; far
// clouds fade into the horizon's haze. Their opacity is marched in one of Godot's reduced sky passes (march;
// sky_volumetric.gdshader half, sky_volumetric_quarter.gdshader a quarter), their light in the full pass, which lays them
// over the sky. The radiance cube map keeps the flat clouds.
#include "sky_modern.gdshaderinc"

uniform sampler2D cover_map : filter_linear_mipmap, repeat_enable;   // the flat sky's cloud density 0..1 over a repeat of `clouds` (CloudNoise.Cover)
uniform sampler2D albedo_map : source_color, filter_linear_mipmap, repeat_enable;   // `clouds` with mipmaps (cloud_albedo's colours)
uniform sampler3D shape_noise : filter_linear, repeat_enable;        // 128^3 Perlin-Worley
uniform sampler3D shape_noise_far : filter_linear, repeat_enable;    // the same shrunk to 32^3, for far clouds (CloudNoise.Shrink)
uniform sampler3D detail_noise : filter_linear, repeat_enable;       // 64^3 Worley
uniform vec3 world_origin;              // game coordinates of Godot's origin (Main.Rebase)
uniform float cloud_time = 0.0;         // the mission's seconds (Main), the wind's clock
uniform float cloud_floor = 1500.0;     // game height of the layer's floor: the map's average ground + 1.5 km
uniform float cloud_depth = 450.0;     // its depth (shallow against the clouds' width: sheets and veils, not heaps)
uniform float planet_radius = 6371000.0;
uniform vec2 wind = vec2(-129.0, -129.0); // m/s over game x, z: the dome's scroll overhead, at the layer's height (MainClouds)
uniform float cover_tile = 5000.0;      // metres a repeat of the sky texture
uniform float shape_tile = 1800.0;      // metres a tile of the shape noise (128^3: 14 m a texel, its finest cells 112 m)
uniform float detail_tile = 450.0;     // 64^3: 7 m a texel
uniform float max_distance = 60000.0;
uniform vec2 fine_reach = vec2(15.0, 60.0);   // the fine noise fades out, and the shape's finest billows with it, as a pixel grows from x to y metres on the layer
uniform float haze_distance = 13000.0;  // far clouds fade into the horizon's colour about this far off (aerial)
uniform float high_fade = 24000.0;      // the high sheet over about this (further, its noise is finer than a pixel)
uniform float extinction = 0.008;     // per metre at density 1
uniform float slant_thin = 0.7;      // 0: Beer's law along the view; 1: a cloud as opaque at any angle as straight up
uniform float light_rise = 0.3;     // the light's way toward the sun climbs this share as steeply
uniform float shadow = 0.5;          // the light let through: exp(-shadow x the cover summed toward the sun) (the flat sky's)
uniform float light_step = 175.0;    // metres between the light's samples toward the sun (the flat sky's 0.035 of its texture)
uniform float rim = 0.7;             // the silver lining's strength (twice the flat sky's: it lights only the thin parts' share)
uniform float lumps = 0.2;           // how far the shape noise moves the cover (the flat sky's detail 0.35 x 1.6)
uniform float fray = 0.5;            // how far the fine noise frays them (most at the edges)
uniform float stretch = 1.5;         // the noise's features this many times longer along the wind
uniform float fibre_stretch = 3.0;   // the fine noise's, longer still
uniform float warp = 200.0;          // metres the coarse noise pushes the cover about
uniform float push_scale = 7200.0;   // metres a repeat of that noise
uniform float shear = 200.0;         // metres the cover moves downwind from the floor to the roof
uniform float min_thick = 0.08;      // the thinnest veil, a share of the layer
uniform int steps = 8;              // steps across the layer at least
uniform int max_steps = 28;         // and at most, steps of max_step metres between (a long, slanting path)
uniform float max_step = 100.0;

// height over the layer's floor of a point at p from the eye at e (game frame), the floor falling d^2/2R with the distance d
float floor_height(vec3 e, vec3 p) { return e.y + p.y - cloud_floor + dot(p.xz, p.xz) / (2.0 * planet_radius); }

// the smallest t >= 0 where the ray's height over the floor, h0 + gy t + k t^2, is x (or -1: never)
float first_at(float h0, float gy, float k, float x) {
	float c = h0 - x;
	if (k < 1e-12) { float t = -c / gy; return (abs(gy) > 1e-6 && t >= 0.0) ? t : -1.0; }
	float disc = gy * gy - 4.0 * k * c;
	if (disc < 0.0) return -1.0;
	float sq = sqrt(disc);
	float q = -0.5 * (gy + (gy >= 0.0 ? sq : -sq));
	float r1 = q / k, r2 = abs(q) > 1e-12 ? c / q : r1;
	float lo = min(r1, r2), hi = max(r1, r2);
	return lo >= 0.0 ? lo : (hi >= 0.0 ? hi : -1.0);
}

// the wind's way over game x, z (its speed is wind)
vec2 wind_dir() { return dot(wind, wind) > 1e-6 ? normalize(wind) : vec2(-0.70710678, -0.70710678); }

// cloud cover at a point p from the eye at e (game frame), its height in the layer (0 floor .. 1 roof) and the noise's place.
// The noise's space is stretched a little along the wind (more combed the clouds), and the cover is looked up a little off the point: pushed
// about by a coarse noise (frayed, fibrous edges) and carried downwind with height (the layer sheared, as cirrus is)
// (cuv: where that is in the sky texture, whose texels the cover was drawn from)
float cover_at_uv(vec3 e, vec3 p, float lod, bool push_it, out float hf, out vec3 q, out float broad, out vec2 cuv) {
	hf = floor_height(e, p) / cloud_depth;
	// carried by the wind: the cover and the noise together
	vec2 w = e.xz + p.xz - wind * cloud_time;
	vec2 wd = wind_dir();
	vec2 ws = w - wd * dot(w, wd) * (1.0 - 1.0 / stretch);
	q = vec3(ws.x, hf * cloud_depth, ws.y);
	vec2 uv = w + wd * (shear * hf);
	if (push_it) {
		// (the same push all the way up: one varying with height laid the layer in strata, lines along every slanting cloud;
		// gentle, a push that changed faster than the distance folded the cover over on itself; and smooth: read linearly
		// the noise creased the cover along its texel grid, parallel lines across the sky, so the place within a texel is
		// eased first, a smooth slope across the grid)
		float nn = float(textureSize(shape_noise_far, 0).x);
		vec2 tc = q.xz / push_scale * nn + 0.5;
		vec2 fc = fract(tc);
		tc = (floor(tc) + fc * fc * (3.0 - 2.0 * fc) - 0.5) / nn;
		vec2 push = vec2(textureLod(shape_noise_far, vec3(tc.x, 0.5 / nn, tc.y), 0.0).r, textureLod(shape_noise_far, vec3(tc.y + 0.37, 9.5 / nn, tc.x + 0.61), 0.0).r) - 0.5;
		uv += push * (2.0 * warp);
	}
	uv /= cover_tile;
	cuv = uv;
	// the cover a few mip levels coarser: the clouds' depth follows it (the fine cover's wisps as the depth of every cloud's
	// floor and roof made them corrugated, and seen slanting each fold was a bright line)
	broad = textureLod(cover_map, uv, lod + 2.5).r;
	return textureLod(cover_map, uv, lod).r;
}
float cover_at(vec3 e, vec3 p, float lod, bool push_it, out float hf, out vec3 q, out float broad) {
	vec2 cuv;
	return cover_at_uv(e, p, lod, push_it, hf, q, broad, cuv);
}

// density from the cover and the noise: the cover moved a little more by 3D lumps and frayed by fibres (as the flat sky reshapes
// its texture), then laid in a sheet: the clouds thin toward their edges into veils, with no flat floor or rounded roof.
// erode adds the fibres, less of them far off (coarse 0 near .. 1 far, 2 farther still, the lumps gone too): finer than a
// pixel or a step they would only sparkle
// the flat sky's cloud density there (0..1, its den: the cover is smoothstep(0.03, 0.75) of it), the shape noise's value in n
float flat_cover(float cov, vec3 q, bool erode, float coarse, out float n) {
	n = 0.5;
	if (cov < 0.01) return 0.0;
	float far = min(coarse, 1.0);
	vec3 sq = q / shape_tile;
	n = far >= 1.0 ? textureLod(shape_noise_far, sq, 0.0).r
		: far <= 0.0 ? textureLod(shape_noise, sq, 0.0).r : mix(textureLod(shape_noise, sq, 0.0).r, textureLod(shape_noise_far, sq, 0.0).r, far);
	// coarser still (1 .. 2), the lumps themselves even out
	n = mix(n, 0.5, max(coarse - 1.0, 0.0));
	// (where the texture has no cloud the noise makes none)
	float v = cov + (n - 0.5) * lumps * min(cov * 5.0, 1.0);
	if (v <= 0.02 - fray * 0.25) return 0.0;
	if (erode && far < 1.0) {
		// fibres: the fine noise drawn out a little further along the wind, strongest at the edges but through the veils too
		vec2 wd = wind_dir();
		vec3 fq = vec3(q.x, q.y * 2.0, q.z);
		fq.xz -= wd * dot(q.xz, wd) * (1.0 - stretch / fibre_stretch);
		float dn = textureLod(detail_noise, fq / detail_tile, 0.0).r;
		float edge = 1.0 - abs(clamp(v, 0.0, 1.0) * 2.0 - 1.0);
		v += (dn - 0.5) * fray * (0.35 + 0.65 * edge) * (1.0 - far);
	}
	return v <= 0.02 ? 0.0 : min(v, 1.0);
}

float shape_density(float cov, float broad, float hf, vec3 q, bool erode, float coarse, float dh, out float v) {
	v = 0.0;
	if (cov < 0.01 || hf <= 0.0 || hf >= 1.0) return 0.0;
	float n;
	v = flat_cover(cov, q, erode, coarse, n);
	if (v <= 0.0) return 0.0;
	float c = smoothstep(0.03, 0.75, v);
	vec3 sq = q / shape_tile;
	// as deep as it is dense, and lumpy: veils at the edges, the cores' roofs raised and floors lowered by the noise, so the
	// sun lights their near sides and they shade their neighbours (the flat sky's relief), with no flat floor or loaf top
	// (the depth's lumps from the noise averaged down: its finer billows made terraces of the floors and roofs too)
	float nb = mix(textureLod(shape_noise_far, sq * 0.5, 0.0).r, 0.5, max(coarse - 1.0, 0.0));
	float thick = clamp(smoothstep(0.03, 0.75, broad) * (0.5 + 1.1 * (nb - 0.35)), min_thick, 1.0);
	float mid = 0.4 + (nb - 0.5) * 0.25;
	// (inside the layer: a cloud cut off at its floor or roof is a hard edge the steps band)
	float lo = max(mid - thick * 0.35, 0.02), hi = min(mid + thick * 0.65, 0.98);
	// its floor and roof eased in over at least what a step of the march climbs (dh, a share of the layer): sharper, the
	// steps would land in or out of them by turns, contour lines along every slanting cloud
	float sheet = smoothstep(lo - dh * 0.5, lo + max(0.2, dh), hf) * smoothstep(hi + dh * 0.5, hi - max(0.3, dh), hf);
	// as dense as lets through, straight up, what the flat sky's cover lets through
	return sheet * -log(1.0 - 0.92 * c) / (extinction * cloud_depth * thick);
}

float hg(float c, float g) {
	float g2 = g * g;
	return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);   // times 4 pi: 1 for an even spread
}

// how much of a cloud's own light reaches the eye from a distance t (the rest is the horizon's colour): the air between
// barely shows near by and takes over past haze_distance (exp(-(t/d)^2): clouds a few kilometres off keep their contrast,
// the far field merges into the horizon's haze as the flat sky's does instead of rows of equally sharp clouds to the edge)
float aerial(float t) { float x = t / haze_distance; return exp(-x * x); }

// the eye in game coordinates from the camera's position (Godot frame)
vec3 game_eye(vec3 cam) { return world_origin + vec3(cam.x, cam.y, -cam.z); }

// the clouds along a view direction from the camera at cam (both Godot frame), in the reduced pass: how much they let
// through (a) and at what height in the layer they are seen (r, 0 floor .. 1 roof: their opacity's mean; with no cloud,
// near the layer's middle, so the full pass's blend between pixels stays sane), g 1 where the view crosses the layer.
// Only their opacity is marched; their light is the full pass's (clouds_light)
vec4 march(vec3 eyedir, vec3 cam) {
	vec3 d = normalize(eyedir);
	// a pixel's angle (this pass's pixels), before any branch
	float pix = max(length(fwidth(d)), 1e-5);
	vec3 g = vec3(d.x, d.y, -d.z);
	vec3 e = game_eye(cam);
	// where the ray is in the layer: its height over the curved floor is h0 + g.y t + k t^2
	float h0 = e.y - cloud_floor, k = dot(g.xz, g.xz) / (2.0 * planet_radius);
	float t0, t1;
	if (h0 < 0.0) { t0 = first_at(h0, g.y, k, 0.0); t1 = first_at(h0, g.y, k, cloud_depth); }
	else if (h0 <= cloud_depth) { t0 = 0.0; float a = first_at(h0, g.y, k, 0.0), b = first_at(h0, g.y, k, cloud_depth); t1 = a < 0.0 ? b : (b < 0.0 ? a : min(a, b)); }
	else { t0 = first_at(h0, g.y, k, cloud_depth); float a = first_at(h0, g.y, k, 0.0); t1 = a >= 0.0 ? a : t0 + 4.0 * cloud_depth / max(abs(g.y), 0.05); }
	if (t0 < 0.0 || t0 >= max_distance) return vec4(0.0, 0.0, 0.0, 1.0);   // r 0: no layer this way
	if (t1 < 0.0) t1 = max_distance;
	t1 = min(t1, max_distance);
	// even steps across the layer, from the middle of each (no per-pixel jitter: in the reduced pass that drew every pixel's
	// share of a step as a block of noise on the screen); a thin layer needs no skipping through clear air
	float len = t1 - t0;
	// the step's length changes smoothly with the path's (a whole number of steps across it changed by one from pixel to
	// pixel, every sample moving at once: a line across the sky at each change, stacked lines over a cloud), the last
	// step whatever is left
	float dt = max(len / float(max_steps), min(max_step, len / float(steps)));
	int n = int(ceil(len / dt - 1e-3));
	float dh = dt * abs(g.y) / cloud_depth;
	// a texel of the cover in metres
	float cover_texel = cover_tile / float(textureSize(cover_map, 0).x);

	// a slanting path crosses the sheet's whole depth sideways: thinned toward the flat sky's look, where a cloud lets through
	// as much low down as overhead (otherwise low clouds turn solid to their very edges, rounded and smooth)
	float slant = pow(clamp(abs(g.y), 0.02, 1.0), slant_thin);
	float tr = 1.0, wt = 0.0;
	for (int i = 0; i < n; i++) {
		if (tr < 0.03) break;
		float sdt = min(dt, t1 - (t0 + float(i) * dt));
		float t = t0 + float(i) * dt + 0.5 * sdt;
		vec3 p = g * t;
		// what a pixel spans on the layer here: detail finer than that would only alias (stairs and crawling edges in the
		// reduced pass), so the cover is read from a coarser mip and the fine noise fades out
		float span = pix * t / sqrt(max(abs(g.y), 0.03));
		// (and twice how far a step goes across the layer: on a slanting path, detail finer than that falls between the
		// steps and is drawn as rows of lines and dots)
		float reach = max(span, 2.0 * dt * length(g.xz));
		float far = smoothstep(fine_reach.x, fine_reach.y, reach) + smoothstep(fine_reach.y * 1.5, fine_reach.y * 4.0, reach);
		float lod = log2(max(reach / cover_texel, 1.0));
		float hf, broad, c; vec3 q;
		float den = shape_density(cover_at(e, p, lod, true, hf, q, broad), broad, hf, q, true, far, dh, c);
		if (den <= 0.0) continue;
		float st = exp(-extinction * den * slant * sdt);
		wt += tr * (1.0 - st) * hf;
		tr *= st;
	}
	return vec4(tr < 0.999 ? clamp(wt / (1.0 - tr), 0.0, 1.0) : 0.45, 1.0, 0.0, tr);
}

// the clouds' light along a view direction, in the full pass, from the reduced pass's v (march): their light in rgb, what
// they let through in a. Lit as the flat sky lights its clouds, one look a pixel at the point they are seen at, at the full
// resolution: the texture's shade where the way toward the sun is through cloud, a little brighter than its cloud colour
// where it is clear (the bright rims of every lump, wisp and swirl facing the sun), the thickest cores darker, and a
// silver lining on the thin parts toward the sun (the moon's cold). (Lit along the march, the light was averaged over
// kilometres of a slanting view and the small lit and shaded sides were lost; and a single look has no steps to band, so
// it keeps the detail a pixel allows.) Behind them, the flat sky's fainter high layer.
vec4 clouds_light(vec3 eyedir, vec3 cam, vec4 v) {
	vec3 d = normalize(eyedir);
	float pix = max(length(fwidth(d)), 1e-5);
	vec3 g = vec3(d.x, d.y, -d.z);
	vec3 e = game_eye(cam);
	vec3 s = normalize(sprite_dir);
	vec3 sg = vec3(s.x, s.y, -s.z);
	// the light's way drawn out sideways (as the flat sky looks for cloud toward the sun across its dome): lumps shade their
	// neighbours and their far sides even with the sun high
	vec3 lg = normalize(vec3(sg.x, sg.y * light_rise, sg.z) + vec3(1e-4, 0.0, 0.0));
	float tr = clamp(v.a, 0.0, 1.0);
	vec3 scat = vec3(0.0);
	// where along this pixel's own view the clouds are seen: where it reaches the height the reduced pass found them at
	// (the distance itself, blended between the reduced pass's pixels, put the shading's samples hundreds of metres off
	// along a low view, by a different amount in each 2x2 block: stair-stepped streaks that crawled in motion)
	float tm = v.g > 0.5 ? first_at(e.y - cloud_floor, g.y, dot(g.xz, g.xz) / (2.0 * planet_radius), clamp(v.r, 0.0, 1.0) * cloud_depth) : -1.0;
	if (tm > 0.0 && tm < max_distance) {
		float cover_texel = cover_tile / float(textureSize(cover_map, 0).x);
		vec3 p = g * tm;
		float span = pix * tm / max(abs(g.y), 0.03);
		float far = smoothstep(fine_reach.x, fine_reach.y, span) + smoothstep(fine_reach.y * 1.5, fine_reach.y * 4.0, span);
		float lod = log2(max(span / cover_texel, 1.0)) - 0.5;
		float hf, broad, n; vec3 q; vec2 cuv;
		float cov = cover_at_uv(e, p, lod, true, hf, q, broad, cuv);
		float keep = aerial(tm);
		// the texture's own colour there, softened as the cover is (over at least what a pixel spans)
		vec3 tex = vec3(0.0);
		if (cloud_albedo > 0.0) {
			// softened as sky_texel softens it, from the mipmap level a pixel spans (a pixel's footprint on the layer stretches
			// as 1/sin(elevation) along the view: low down a level of its short side's size aliased as stair-stepped streaks)
			float r = max(0.006, span / cover_tile);
			float al = log2(max(r * float(textureSize(albedo_map, 0).x), 1.0));
			tex = (textureLod(albedo_map, cuv, al).rgb * 0.4 + (textureLod(albedo_map, cuv + vec2(r, 0.0), al).rgb + textureLod(albedo_map, cuv - vec2(r, 0.0), al).rgb
				+ textureLod(albedo_map, cuv + vec2(0.0, r), al).rgb + textureLod(albedo_map, cuv - vec2(0.0, r), al).rgb) * 0.15);
		}
		float c = flat_cover(cov, q, true, far, n);
		if (tr < 0.999) {
			// toward the light, as the flat sky looks: the cloud at a few points further out toward the sun (whatever their
			// height), the first with the fine noise
			float acc = 0.0;
			for (int j = 1; j <= 3; j++) {
				float lhf, lb, ln; vec3 lq;
				acc += flat_cover(cover_at(e, p + lg * (light_step * float(j)), lod, j == 1, lhf, lq, lb), lq, j == 1, far, ln);
			}
			float lit = exp(-acc * shadow);
			float core = smoothstep(0.2, 0.95, c);
			vec3 lum = cloud_light(tex, lit, core, dot(d, s), false);
			// far off, the air between: the cloud's light gives way to the horizon's colour
			lum = mix(I76_COL(horizon_color), lum, keep);
			// drawn over the sky as the flat sky draws its clouds: their cover is what they stop of the light over 0.92 (the
			// flat sky lets 8% of the sun through a whole cloud but paints the cloud over the sky entirely)
			tr = 1.0 - min((1.0 - tr) / 0.92, 1.0);
			// and thinning into the air: far off the rows of clouds merge into the sky's own haze rather than a band of
			// their colour along the horizon
			tr = 1.0 - (1.0 - tr) * sqrt(keep);
			scat = lum * (1.0 - tr);
		}
		// between them, the texture's own sky (sky_modern's flat clouds do the same), a veil over the gradient: wherever the
		// view crosses the layer, cloud or not (only where there was cloud, the clear pixels between showed the measured
		// colour as flat patches with the reduced pass's stair-stepped edges)
		float k = cloud_albedo * sky_own * tr * keep;
		scat += k * tex;
		tr *= 1.0 - k;
	}
	// behind them, the flat sky's fainter high layer (sky_modern), here a sheet fixed in the world 8 km up, drifting slower
	if (g.y > 0.0) {
		float th = (cloud_floor + 6500.0 - e.y) / g.y;
		if (th > 0.0) {
			vec2 hw = e.xz + g.xz * th - wind * 0.4 * cloud_time;
			float high = smoothstep(0.3, 0.9, density(hw / (cover_tile * 2.0) + vec2(0.37, 0.61), 3)) * 0.35 * exp(-th / high_fade);
			scat += tr * high * mix(I76_COL(cloud_color), I76_COL(horizon_color), 0.3);
			tr *= 1.0 - high;
		}
	}
	return vec4(scat, tr);
}

void sky() {
	if (I76_VOL_PASS) {
		// the cube map's own reduced pass is not used: nothing to march
		vec4 v = AT_CUBEMAP_PASS ? vec4(0.0, 0.0, 0.0, 1.0) : march(EYEDIR, POSITION);
		COLOR = v.rgb;
		ALPHA = v.a;
	} else if (AT_CUBEMAP_PASS) {
		// the radiance (ambient light and reflections) keeps the flat clouds
		COLOR = modern_sky(EYEDIR, TIME, true);
	} else {
		COLOR = modern_sky_with(EYEDIR, TIME, false, true, clouds_light(EYEDIR, POSITION, I76_VOL_COLOR));
	}
}
