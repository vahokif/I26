// The modern sky with volumetric clouds, the march and the composite (Graphic Detail's Clouds: Volumetric; the modern look on
// Forward+ and Mobile). The sky, sun, moon and haze are sky_modern's; the flat cloud layer is replaced by a thin layer of
// clouds fixed in the world (not centred on the eye as the original's dome is), 1.5 km over the map's average ground, its
// floor curving down with the Earth's radius so distant clouds meet the horizon. The cover is the flat sky's own cloud
// density laid flat over the world (cover_map, CloudNoise.Cover: the mission's sky texture ramped as the flat sky ramps it,
// moved by the same lumps and frayed by the same fine noise), a repeat every cover_tile metres, carried by a wind as fast as
// the dome's texture scrolls and in its direction, on the mission's clock (cloud_time), so every player sees the same clouds
// in the same place. The clouds are a sheet as dense as lets through, looking straight up, what the flat sky's cover lets
// through, thinner toward their edges; tiling 3D noise (CloudNoise: Perlin-Worley lumps, Worley fibres), drawn out along the
// wind, the layer sheared downwind with height and the cover pushed about by a coarse noise, makes them streaky and frayed.
// Lighting: Beer's law with a powder term, a two-lobe Henyey-Greenstein phase (the silver lining round the sun), a few
// samples toward the sun or moon, three scattering octaves, and the sky's measured colours as the ambient; far clouds fade
// into the horizon's haze. The march runs in one of Godot's reduced sky passes (sky_volumetric.gdshader half,
// sky_volumetric_quarter.gdshader a quarter); the full pass lays it over the sky. The radiance cube map keeps the flat clouds.
#include "sky_modern.gdshaderinc"

uniform sampler2D cover_map : filter_linear_mipmap, repeat_enable;   // the flat sky's cloud density 0..1 over a repeat of `clouds` (CloudNoise.Cover)
uniform sampler3D shape_noise : filter_linear, repeat_enable;        // 128^3 Perlin-Worley
uniform sampler3D shape_noise_far : filter_linear, repeat_enable;    // the same shrunk to 32^3, for far clouds (CloudNoise.Shrink)
uniform sampler3D detail_noise : filter_linear, repeat_enable;       // 64^3 Worley
uniform vec3 world_origin;              // game coordinates of Godot's origin (Main.Rebase)
uniform float cloud_time = 0.0;         // the mission's seconds (Main), the wind's clock
uniform float cloud_floor = 1500.0;     // game height of the layer's floor: the map's average ground + 1.5 km
uniform float cloud_depth = 150.0;     // its thickness (thin against the clouds' width: sheets and veils, not heaps)
uniform float planet_radius = 6371000.0;
uniform vec2 wind = vec2(-45.0, -45.0); // m/s over game x, z: the dome's texture scroll at its 1500 m a repeat
uniform float cover_tile = 5000.0;      // metres a repeat of the sky texture
uniform float shape_tile = 1800.0;      // metres a tile of the shape noise (128^3: 14 m a texel, its finest cells 112 m)
uniform float detail_tile = 450.0;     // 64^3: 7 m a texel
uniform float max_distance = 60000.0;
uniform vec2 fine_reach = vec2(15.0, 60.0);   // the fine noise fades out, and the shape's finest billows with it, as a pixel grows from x to y metres on the layer
uniform float haze_distance = 12000.0;  // far clouds fade into the horizon's colour over about this
uniform float extinction = 0.008;     // per metre at density 1
uniform float slant_thin = 0.6;      // 0: Beer's law along the view; 1: a cloud as opaque at any angle as straight up
uniform float shadow = 3.0;          // the light's path toward the sun as many times denser (the flat sky's clouds shade their cores as deeply)
uniform float sun_gain = 0.65;
uniform float lumps = 0.2;           // how far the shape noise moves the cover (the flat sky's detail 0.35 x 1.6)
uniform float fray = 0.8;            // how far the fine noise frays them (most at the edges)
uniform float stretch = 3.0;         // the noise's features this many times longer along the wind
uniform float fibre_stretch = 8.0;   // the fine noise's, longer still
uniform float warp = 250.0;          // metres the coarse noise pushes the cover about
uniform float shear = 200.0;         // metres the cover moves downwind from the floor to the roof
uniform float ambient_gain = 1.0;
uniform int steps = 10;             // steps across the layer at least
uniform int max_steps = 32;         // and at most, steps of max_step metres between (a long, slanting path)
uniform float max_step = 90.0;

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
// The noise's space is stretched along the wind (streaks), and the cover is looked up a little off the point: pushed
// about by a coarse noise (frayed, fibrous edges) and carried downwind with height (the layer sheared, as cirrus is)
float cover_at(vec3 e, vec3 p, float lod, bool push_it, out float hf, out vec3 q) {
	hf = floor_height(e, p) / cloud_depth;
	// carried by the wind: the cover and the noise together
	vec2 w = e.xz + p.xz - wind * cloud_time;
	vec2 wd = wind_dir();
	vec2 ws = w - wd * dot(w, wd) * (1.0 - 1.0 / stretch);
	q = vec3(ws.x, hf * cloud_depth, ws.y);
	vec2 uv = w + wd * (shear * hf);
	if (push_it) {
		vec3 wq = q / (shape_tile * 1.3);
		uv += (vec2(textureLod(shape_noise_far, wq, 0.0).r, textureLod(shape_noise_far, wq.zxy + vec3(0.31, 0.57, 0.13), 0.0).r) - 0.5) * (2.0 * warp);
	}
	uv /= cover_tile;
	return textureLod(cover_map, uv, lod).r;
}

// density from the cover and the noise: the cover moved a little more by 3D lumps and frayed by fibres (as the flat sky reshapes
// its texture), then laid in a sheet: the clouds thin toward their edges into veils, with no flat floor or rounded roof.
// erode adds the fibres, less of them far off (0 near .. 1 far): finer than a pixel they would only sparkle
float shape_density(float cov, float hf, vec3 q, bool erode, float far) {
	if (cov < 0.01 || hf <= 0.0 || hf >= 1.0) return 0.0;
	vec3 sq = q / shape_tile;
	float n = far >= 1.0 ? textureLod(shape_noise_far, sq, 0.0).r
		: far <= 0.0 ? textureLod(shape_noise, sq, 0.0).r : mix(textureLod(shape_noise, sq, 0.0).r, textureLod(shape_noise_far, sq, 0.0).r, far);
	// (where the texture has no cloud the noise makes none)
	float v = cov + (n - 0.5) * lumps * min(cov * 5.0, 1.0);
	if (v <= 0.02 - fray * 0.25) return 0.0;
	if (erode && far < 1.0) {
		// fibres: the fine noise drawn out further along the wind, strongest at the edges but through the veils too
		vec2 wd = wind_dir();
		vec3 fq = vec3(q.x, q.y * 2.0, q.z);
		fq.xz -= wd * dot(q.xz, wd) * (1.0 - stretch / fibre_stretch);
		float dn = textureLod(detail_noise, fq / detail_tile, 0.0).r;
		float edge = 1.0 - abs(clamp(v, 0.0, 1.0) * 2.0 - 1.0);
		v += (dn - 0.5) * fray * (0.35 + 0.65 * edge) * (1.0 - far);
	}
	if (v <= 0.02) return 0.0;
	// the flat sky's cover, eased in a little more at the edges (seen slanting, a sheet's edge would read as a crisp rim)
	float c = pow(smoothstep(0.02, 0.8, v), 1.5);
	// a thin sheet, a little thicker where the cloud is denser, its middle wandering with the noise
	float mid = 0.5 + (n - 0.5) * 0.3;
	float thick = mix(0.4, 1.0, c);
	float sheet = smoothstep(0.0, 0.12, 0.5 * thick - abs(hf - mid));
	// as dense as lets through, straight up, what the flat sky's cover lets through
	return sheet * -log(1.0 - 0.92 * c) / (extinction * cloud_depth * thick);
}

float hg(float c, float g) {
	float g2 = g * g;
	return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);   // times 4 pi: 1 for an even spread
}

// the clouds along a view direction from the camera at cam (both Godot frame): their light in rgb, the transmittance in a
vec4 march(vec3 eyedir, vec3 cam) {
	vec3 d = normalize(eyedir);
	// a pixel's angle (this pass's pixels), before any branch
	float pix = max(length(fwidth(d)), 1e-5);
	vec3 g = vec3(d.x, d.y, -d.z);
	vec3 e = world_origin + vec3(cam.x, cam.y, -cam.z);   // the eye in game coordinates
	vec3 s = normalize(sprite_dir);
	vec3 sg = vec3(s.x, s.y, -s.z);
	// where the ray is in the layer: its height over the curved floor is h0 + g.y t + k t^2
	float h0 = e.y - cloud_floor, k = dot(g.xz, g.xz) / (2.0 * planet_radius);
	float t0, t1;
	if (h0 < 0.0) { t0 = first_at(h0, g.y, k, 0.0); t1 = first_at(h0, g.y, k, cloud_depth); }
	else if (h0 <= cloud_depth) { t0 = 0.0; float a = first_at(h0, g.y, k, 0.0), b = first_at(h0, g.y, k, cloud_depth); t1 = a < 0.0 ? b : (b < 0.0 ? a : min(a, b)); }
	else { t0 = first_at(h0, g.y, k, cloud_depth); float a = first_at(h0, g.y, k, 0.0); t1 = a >= 0.0 ? a : t0 + 4.0 * cloud_depth / max(abs(g.y), 0.05); }
	if (t0 < 0.0 || t0 >= max_distance) return vec4(0.0, 0.0, 0.0, 1.0);
	if (t1 < 0.0) t1 = max_distance;
	t1 = min(t1, max_distance);
	// even steps across the layer, from the middle of each (no per-pixel jitter: in the reduced pass that drew every pixel's
	// share of a step as a block of noise on the screen); a thin layer needs no skipping through clear air
	float len = t1 - t0;
	int n = int(clamp(len / max_step, float(steps), float(max_steps)));
	float dt = len / float(n);
	// a texel of the cover in metres
	float cover_texel = cover_tile / float(textureSize(cover_map, 0).x);

	// a slanting path crosses the sheet's whole depth sideways: thinned toward the flat sky's look, where a cloud lets through
	// as much low down as overhead (otherwise low clouds turn solid to their very edges, rounded and smooth)
	float slant = pow(clamp(abs(g.y), 0.02, 1.0), slant_thin);
	float cs = dot(d, s);
	// two lobes: forward (the silver lining) and a little back
	float ph = mix(hg(cs, 0.6), hg(cs, -0.25), 0.3);
	// (the moon a little brighter than its light on the ground: the flat sky draws its clouds in the texture's own colours)
	vec3 sun_col = I76_COL(cloud_color) * sun_gain * sqrt(light_strength);
	vec3 sky_amb = mix(I76_COL(horizon_color), I76_COL(zenith_color), 0.5);
	vec3 amb_lo = I76_COL(cloud_shade) * 0.85, amb_hi = mix(mix(I76_COL(cloud_shade), I76_COL(cloud_color), 0.35), sky_amb, 0.25);
	vec3 haze = I76_COL(horizon_color);

	vec3 scat = vec3(0.0);
	float tr = 1.0;
	for (int i = 0; i < n; i++) {
		if (tr < 0.03) break;
		float t = t0 + (float(i) + 0.5) * dt;
		vec3 p = g * t;
		// what a pixel spans on the layer here: detail finer than that would only alias (stairs and crawling edges in the
		// reduced pass), so the cover is read from a coarser mip and the fine noise fades out
		float span = pix * t / sqrt(max(abs(g.y), 0.03));
		float far = smoothstep(fine_reach.x, fine_reach.y, span);
		float lod = log2(max(span / cover_texel, 1.0));
		float hf; vec3 q;
		float cov = cover_at(e, p, lod, true, hf, q);
		float den = shape_density(cov, hf, q, true, far);
		if (den <= 0.0) continue;
		// toward the light: a few samples, each further out, the far ones without the fine noise
		float od = 0.0, ls = 40.0, lt = 0.0;
		for (int j = 0; j < 4; j++) {
			lt += ls;
			float lhf; vec3 lq;
			float lc = cover_at(e, p + sg * lt, lod, j == 0, lhf, lq);
			od += shape_density(lc, lhf, lq, j == 0, far) * ls;
			ls *= 2.2;
		}
		float sig = extinction * den * slant;
		// three scattering octaves (each weaker, its extinction and phase softer), and the powder darkening of the edges facing away from the light
		float powder = 1.0 - exp(-den * extinction * shadow * 160.0);
		float pw = mix(1.0, powder, 0.5 - 0.5 * cs);
		float light = 0.0, a = 1.0, b = 1.0, c = 1.0;
		for (int kk = 0; kk < 3; kk++) {
			light += b * exp(-extinction * shadow * od * a) * mix(1.0, min(ph, 3.0), c);
			a *= 0.3; b *= 0.45; c *= 0.5;
		}
		vec3 amb = mix(amb_lo, amb_hi, clamp(0.3 + hf * 0.6, 0.0, 1.0)) * ambient_gain;
		vec3 lum = sun_col * (light / 1.65) * pw + amb;   // the octaves' sum is 1.65 in clear air
		// far off, the haze between: the cloud's light gives way to the horizon's colour
		lum = mix(haze, lum, exp(-t / haze_distance));
		float st = exp(-sig * dt);
		scat += tr * lum * (1.0 - st);
		tr *= st;
	}
	// behind them, the flat sky's fainter high layer (sky_modern), here a sheet fixed in the world 8 km up, drifting slower
	if (g.y > 0.0) {
		float th = (cloud_floor + 6500.0 - e.y) / g.y;
		if (th > 0.0) {
			vec2 hw = e.xz + g.xz * th - wind * 0.4 * cloud_time;
			float high = smoothstep(0.3, 0.9, density(hw / (cover_tile * 2.0) + vec2(0.37, 0.61), 3)) * 0.35 * exp(-th / (haze_distance * 2.0));
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
		COLOR = modern_sky_with(EYEDIR, TIME, false, true, I76_VOL_COLOR);
	}
}
