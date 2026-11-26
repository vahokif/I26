// The modern sky with volumetric clouds, the march and the composite (Graphic Detail's Clouds: Volumetric; the modern look on
// Forward+ and Mobile). The sky, sun, moon and haze are sky_modern's; the flat cloud layer is replaced by a layer of clouds
// fixed in the world (not centred on the eye as the original's dome is), from 1.5 to 3 km over the map's average ground,
// its floor curving down with the Earth's radius so distant clouds meet the horizon. The cover is the mission's own sky
// texture laid flat over the world (cover_map, its brightness ramped as the flat sky ramps it, CloudNoise.Cover), a repeat
// every cover_tile metres, carried by a wind as fast as the dome's texture scrolls and in its direction, on the mission's
// clock (cloud_time), so every player sees the same clouds in the same place. Inside the cover, tiling 3D noise (CloudNoise:
// Perlin-Worley shape, Worley erosion) shapes them, flat-bottomed with rounder tops that rise where the cover is denser.
// Lighting: Beer's law with a powder term, a two-lobe Henyey-Greenstein phase (the silver lining round the sun), a few
// samples toward the sun or moon, three scattering octaves, and the sky's measured colours as the ambient; far clouds fade
// into the horizon's haze. The march runs in one of Godot's reduced sky passes (sky_volumetric.gdshader a quarter,
// sky_volumetric_half.gdshader half); the full pass lays it over the sky. The radiance cube map keeps the flat clouds.
#include "sky_modern.gdshaderinc"

uniform sampler2D cover_map : filter_linear, repeat_enable;   // cloud cover 0..1 in the layout of `clouds`: R softened a little, G blurred wide (CloudNoise.Cover)
uniform sampler3D shape_noise : filter_linear, repeat_enable;        // 128^3 Perlin-Worley
uniform sampler3D shape_noise_far : filter_linear, repeat_enable;    // the same shrunk to 32^3, for far clouds (CloudNoise.Shrink)
uniform sampler3D detail_noise : filter_linear, repeat_enable;       // 32^3 Worley
uniform vec3 world_origin;              // game coordinates of Godot's origin (Main.Rebase)
uniform float cloud_time = 0.0;         // the mission's seconds (Main), the wind's clock
uniform float cloud_floor = 1500.0;     // game height of the layer's floor: the map's average ground + 1.5 km
uniform float cloud_depth = 1500.0;     // its thickness
uniform float planet_radius = 6371000.0;
uniform vec2 wind = vec2(-45.0, -45.0); // m/s over game x, z: the dome's texture scroll at its 1500 m a repeat
uniform float cover_tile = 8000.0;      // metres a repeat of the sky texture
uniform float shape_tile = 1800.0;      // metres a tile of the shape noise
uniform float detail_tile = 450.0;
uniform float max_distance = 60000.0;
uniform float far_detail = 8000.0;     // the fine noise gone by this distance, the shape's finest billows with it
uniform float haze_distance = 22000.0;  // far clouds fade into the horizon's colour over about this
uniform float extinction = 0.04;      // per metre at density 1
uniform float erosion = 0.6;
uniform float sun_gain = 0.9;
uniform float cover_reach = 0.75;     // the texture's cover at which the clouds are whole (the flat sky's noise spreads them about as far)
uniform float ambient_gain = 1.0;
uniform int steps = 40;             // coarse strides across the layer
uniform float fine_step = 0.4;       // in cloud, steps of this share of a stride

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

// cloud cover, height in the layer (0 floor .. 1 roof) and the noise's place, for a point p from the eye at e (game frame)
float cover_at(vec3 e, vec3 p, out float hf, out vec3 q, out float wide) {
	hf = floor_height(e, p) / cloud_depth;
	// carried by the wind: the cover and the noise together
	vec2 w = e.xz + p.xz - wind * cloud_time;
	vec2 uv = w / cover_tile;
	q = vec3(w.x, hf * cloud_depth * 0.5, w.y);   // the noise's lumps half again as tall as wide
	// the cover blurred wide: the clouds' height follows it, so they narrow as they rise and their tops round over
	vec2 c2 = textureLod(cover_map, uv, 0.0).rg;
	wide = c2.g;
	return smoothstep(0.0, cover_reach, c2.r);
}

float remap01(float v, float lo, float hi) { return clamp((v - lo) / max(hi - lo, 1e-4), 0.0, 1.0); }

// density 0..1 from the cover and the noise; erode adds the fine noise, less of it for far (0 near .. 1 far): its billows
// would be under a pixel there and only sparkle
float shape_density(float cov, float wide, float hf, vec3 q, bool erode, float far) {
	if (cov < 0.01 || hf < 0.0 || hf > 1.0) return 0.0;
	// flat bottoms, the tops rounded and higher where the cover is denser
	float top = mix(0.1, 1.0, smoothstep(0.0, 0.8, wide));
	float base = cov * smoothstep(0.0, 0.07, hf) * smoothstep(top, top * 0.3, hf);
	vec3 sq = q / shape_tile;
	float n = far >= 1.0 ? textureLod(shape_noise_far, sq, 0.0).r
		: far <= 0.0 ? textureLod(shape_noise, sq, 0.0).r : mix(textureLod(shape_noise, sq, 0.0).r, textureLod(shape_noise_far, sq, 0.0).r, far);
	float d = remap01(n, 1.0 - base, 1.0) * base;
	if (erode && d > 0.0 && far < 1.0) {
		float dn = textureLod(detail_noise, q / detail_tile, 0.0).r;
		// wispy at the bottom, billowing toward the top
		float m = mix(dn, 1.0 - dn, clamp(hf * 4.0, 0.0, 1.0));
		d = remap01(d, m * erosion * (1.0 - far), 1.0);
	}
	return d;
}

float hg(float c, float g) {
	float g2 = g * g;
	return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);   // times 4 pi: 1 for an even spread
}

// the clouds along a view direction from the camera at cam (both Godot frame): their light in rgb, the transmittance in a
vec4 march(vec3 eyedir, vec3 cam, vec2 frag) {
	vec3 d = normalize(eyedir);
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
	// strides grow with the distance (far clouds are small on the screen and deep in the haze)
	float dt0 = clamp((t1 - t0) / float(steps), 40.0, 250.0);
	// interleaved gradient noise: each pixel starts a different share of a step in
	float jitter = fract(52.9829189 * fract(dot(frag, vec2(0.06711056, 0.00583715))));
	float t = t0 + dt0 * jitter;

	float cs = dot(d, s);
	// two lobes: forward (the silver lining) and a little back
	float ph = mix(hg(cs, 0.6), hg(cs, -0.25), 0.3);
	vec3 sun_col = I76_COL(cloud_color) * sun_gain * light_strength;
	vec3 sky_amb = mix(I76_COL(horizon_color), I76_COL(zenith_color), 0.5);
	vec3 amb_lo = I76_COL(cloud_shade) * 0.85, amb_hi = mix(mix(I76_COL(cloud_shade), I76_COL(cloud_color), 0.35), sky_amb, 0.25);
	vec3 haze = I76_COL(horizon_color);

	vec3 scat = vec3(0.0);
	float tr = 1.0;
	// coarse strides through clear air (doubled where there is no cover at all), back one and fine steps once in cloud,
	// coarse again after a run of empty fine steps
	bool inside = false;
	int empty = 0;
	for (int i = 0; i < 192; i++) {
		if (t > t1 || tr < 0.03) break;
		float dt = dt0 + t * 0.015;
		float fdt = dt * fine_step;
		vec3 p = g * t;
		float far = smoothstep(far_detail * 0.3, far_detail, t);
		float hf, wide; vec3 q;
		float cov = cover_at(e, p, hf, q, wide);
		if (cov < 0.01 && !inside) { t += dt * 2.0; continue; }
		if (!inside) {
			if (shape_density(cov, wide, hf, q, false, far) <= 0.0) { t += dt; continue; }
			inside = true;
			empty = 0;
			t = max(t - dt, t0) + fdt * jitter;
			continue;
		}
		float den = shape_density(cov, wide, hf, q, true, far);
		if (den > 0.0) {
			empty = 0;
			// toward the light: a few samples, each further out, the far ones without the fine noise
			float od = 0.0, ls = 50.0, lt = 0.0;
			for (int j = 0; j < 4; j++) {
				lt += ls;
				float lhf, lw; vec3 lq;
				float lc = cover_at(e, p + sg * lt, lhf, lq, lw);
				od += shape_density(lc, lw, lhf, lq, j == 0, far) * ls;
				ls *= 2.2;
			}
			float sig = extinction * den;
			// three scattering octaves (each weaker, its extinction and phase softer), and the powder darkening of the edges facing away from the light
			float powder = 1.0 - exp(-den * extinction * 160.0);
			float pw = mix(1.0, powder, 0.5 - 0.5 * cs);
			float light = 0.0, a = 1.0, b = 1.0, c = 1.0;
			for (int kk = 0; kk < 3; kk++) {
				light += b * exp(-extinction * od * a) * mix(1.0, min(ph, 3.0), c);
				a *= 0.3; b *= 0.45; c *= 0.5;
			}
			vec3 amb = mix(amb_lo, amb_hi, clamp(hf * 1.4, 0.0, 1.0)) * ambient_gain;
			vec3 lum = sun_col * (light / 1.65) * pw + amb;   // the octaves' sum is 1.65 in clear air
			// far off, the haze between: the cloud's light gives way to the horizon's colour
			lum = mix(haze, lum, exp(-t / haze_distance));
			float st = exp(-sig * fdt);
			scat += tr * lum * (1.0 - st);
			tr *= st;
		} else if (++empty > 8) inside = false;
		t += fdt;
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
		vec4 v = AT_CUBEMAP_PASS ? vec4(0.0, 0.0, 0.0, 1.0) : march(EYEDIR, POSITION, FRAGCOORD.xy);
		COLOR = v.rgb;
		ALPHA = v.a;
	} else if (AT_CUBEMAP_PASS) {
		// the radiance (ambient light and reflections) keeps the flat clouds
		COLOR = modern_sky(EYEDIR, TIME, true);
	} else {
		COLOR = modern_sky_with(EYEDIR, TIME, false, true, I76_VOL_COLOR);
	}
}
