// The modern sky with volumetric clouds, the march and the composite (Graphic Detail's Clouds: Volumetric; the modern look on Forward+ and Mobile).
// The sky, sun, moon and haze are sky_modern's; the flat cloud layer is replaced by clouds raymarched through a shell
// 1.5 to 3 km over the eye, concentric with the engine's dome (radius 6000, centred 5400 below the eye). The cover is the
// mission's own sky texture (cover_map, its brightness ramped as the flat sky ramps it, CloudNoise.Cover): a point in the
// shell takes the cover of the point straight under it on the shell's floor, looked up by the direction the eye sees that
// point in through the dome's projection, so the clouds' bottoms sit exactly where the original's clouds are. Inside the
// cover, tiling 3D noise (CloudNoise: Perlin-Worley shape, Worley erosion) shapes them, flat-bottomed with rounder tops
// that rise where the cover is denser; all of it scrolls with the dome's texture. Lighting: Beer's law with a powder
// term, a two-lobe Henyey-Greenstein phase (the silver lining round the sun), a few samples toward the sun or moon,
// three scattering octaves, and the sky's measured colours as the ambient. The march runs in one of Godot's reduced sky
// passes (sky_volumetric.gdshader a quarter, sky_volumetric_half.gdshader half); the full pass lays it over the sky.
// The radiance cube map keeps the flat clouds (sky_modern.gdshader).
#include "sky_modern.gdshaderinc"

uniform sampler2D cover_map : filter_linear, repeat_enable;   // cloud cover 0..1 in the layout of `clouds`: R softened a little, G blurred wide (CloudNoise.Cover)
uniform sampler3D shape_noise : filter_linear, repeat_enable;        // 128^3 Perlin-Worley
uniform sampler3D detail_noise : filter_linear, repeat_enable;       // 32^3 Worley
uniform float cloud_bottom = 1500.0;   // metres over the eye at the zenith
uniform float cloud_top = 3000.0;
uniform float shape_tile = 0.35;        // dome uv units per tile of the shape noise (one uv unit is a repeat of the sky texture)
uniform float detail_tile = 0.06;
uniform float extinction = 0.04;      // per metre at density 1
uniform float erosion = 0.6;
uniform float sun_gain = 0.9;
uniform float cover_reach = 0.75;     // the texture's cover at which the clouds are whole (the flat sky's noise spreads them about as far)
uniform float ambient_gain = 1.0;
uniform int steps = 40;             // coarse strides across the shell
uniform float fine_step = 0.4;       // in cloud, steps of this share of a stride

// the shell: spheres about the dome's centre
float shell_exit(vec3 g, float r) {
	float b = g.y * drop;
	return -b + sqrt(b * b - (drop * drop - r * r));
}

// cloud cover, height in the shell (0 floor .. 1 roof) and the noise's place, for a point (game frame, eye at the origin)
float cover_at(vec3 p, float off, out float hf, out vec3 q, out float wide) {
	vec3 c = p + vec3(0.0, drop, 0.0);     // from the dome's centre
	float r = length(c);
	float rb = drop + cloud_bottom, rt = drop + cloud_top;
	hf = (r - rb) / (rt - rb);
	// the floor's point under it, then the dome's projection of the direction the eye sees that in
	vec3 b = c * (rb / r) - vec3(0.0, drop, 0.0);
	vec2 uv = (dome_xz(normalize(b)) / radius + 0.5) * 4.0 + vec2(off);
	// the noise in the same uv, height in uv units (a uv unit is about 7.5 km at the floor overhead)
	q = vec3(uv.x, hf * (cloud_top - cloud_bottom) / 7500.0, uv.y);
	// the cover blurred wide: the clouds' height follows it, so they narrow as they rise and their tops round over
	vec2 c2 = textureLod(cover_map, uv, 0.0).rg;
	wide = c2.g;
	return smoothstep(0.0, cover_reach, c2.r);
}

float remap01(float v, float lo, float hi) { return clamp((v - lo) / max(hi - lo, 1e-4), 0.0, 1.0); }

// density 0..1 from the cover and the noise; erode adds the fine noise
float shape_density(float cov, float wide, float hf, vec3 q, bool erode) {
	if (cov < 0.01 || hf < 0.0 || hf > 1.0) return 0.0;
	// flat bottoms, the tops rounded and higher where the cover is denser
	float top = mix(0.1, 1.0, smoothstep(0.0, 0.8, wide));
	float base = cov * smoothstep(0.0, 0.07, hf) * smoothstep(top, top * 0.3, hf);
	float n = textureLod(shape_noise, q / shape_tile, 0.0).r;
	float d = remap01(n, 1.0 - base, 1.0) * base;
	if (erode && d > 0.0) {
		float dn = textureLod(detail_noise, q / detail_tile, 0.0).r;
		// wispy at the bottom, billowing toward the top
		float m = mix(dn, 1.0 - dn, clamp(hf * 4.0, 0.0, 1.0));
		d = remap01(d, m * erosion, 1.0);
	}
	return d;
}

float hg(float c, float g) {
	float g2 = g * g;
	return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);   // times 4 pi: 1 for an even spread
}

// the clouds along a view direction (Godot frame): their light in rgb, the transmittance in a
vec4 march(vec3 eyedir, float time, vec2 frag) {
	vec3 d = normalize(eyedir);
	vec3 g = vec3(d.x, d.y, -d.z);
	if (g.y < 0.0) return vec4(0.0, 0.0, 0.0, 1.0);   // under the horizon the haze has it all (sky_modern)
	vec3 s = normalize(sprite_dir);
	vec3 sg = vec3(s.x, s.y, -s.z);
	float off = time * scroll_per_second;
	float t0 = shell_exit(g, drop + cloud_bottom), t1 = shell_exit(g, drop + cloud_top);
	float dt = (t1 - t0) / float(steps);
	// interleaved gradient noise: each pixel starts a different share of a step in
	float jitter = fract(52.9829189 * fract(dot(frag, vec2(0.06711056, 0.00583715))));
	float t = t0 + dt * jitter;

	float cs = dot(d, s);
	// two lobes: forward (the silver lining) and a little back
	float ph = mix(hg(cs, 0.6), hg(cs, -0.25), 0.3);
	vec3 sun_col = I76_COL(cloud_color) * sun_gain * light_strength;
	vec3 sky_amb = mix(I76_COL(horizon_color), I76_COL(zenith_color), 0.5);
	vec3 amb_lo = I76_COL(cloud_shade) * 0.85, amb_hi = mix(mix(I76_COL(cloud_shade), I76_COL(cloud_color), 0.35), sky_amb, 0.25);

	vec3 scat = vec3(0.0);
	float tr = 1.0;
	// coarse strides through clear air (doubled where there is no cover at all), back one and fine steps once in cloud,
	// coarse again after a run of empty fine steps
	float fdt = dt * fine_step;
	bool inside = false;
	int empty = 0;
	for (int i = 0; i < 192; i++) {
		if (t > t1 || tr < 0.03) break;
		vec3 p = g * t;
		float hf, wide; vec3 q;
		float cov = cover_at(p, off, hf, q, wide);
		if (cov < 0.01 && !inside) { t += dt * 2.0; continue; }
		if (!inside) {
			if (shape_density(cov, wide, hf, q, false) <= 0.0) { t += dt; continue; }
			inside = true;
			empty = 0;
			t = max(t - dt, t0) + fdt * jitter;
			continue;
		}
		float den = shape_density(cov, wide, hf, q, true);
		if (den > 0.0) {
			empty = 0;
			// toward the light: a few samples, each further out, the far ones without the fine noise
			float od = 0.0, ls = 50.0, lt = 0.0;
			for (int j = 0; j < 4; j++) {
				lt += ls;
				float lhf, lw; vec3 lq;
				float lc = cover_at(p + sg * lt, off, lhf, lq, lw);
				od += shape_density(lc, lw, lhf, lq, j == 0) * ls;
				ls *= 2.2;
			}
			float sig = extinction * den;
			// three scattering octaves (each weaker, its extinction and phase softer), and the powder darkening of the edges facing away from the light
			float powder = 1.0 - exp(-den * extinction * 160.0);
			float pw = mix(1.0, powder, 0.5 - 0.5 * cs);
			float light = 0.0, a = 1.0, b = 1.0, c = 1.0;
			for (int k = 0; k < 3; k++) {
				light += b * exp(-extinction * od * a) * mix(1.0, min(ph, 3.0), c);
				a *= 0.3; b *= 0.45; c *= 0.5;
			}
			vec3 amb = mix(amb_lo, amb_hi, clamp(hf * 1.4, 0.0, 1.0)) * ambient_gain;
			vec3 lum = sun_col * (light / 1.65) * pw + amb;   // the octaves' sum is 1.65 in clear air
			float st = exp(-sig * fdt);
			scat += tr * lum * (1.0 - st);
			tr *= st;
		} else if (++empty > 8) inside = false;
		t += fdt;
	}
	// behind them, the flat sky's fainter high layer (sky_modern), drawn here at the march's resolution
	if (g.y > 0.0) {
		vec2 uv2 = (dome_xz(g) / radius + 0.5) * 2.0 + vec2(off * 0.4, -off * 0.25) + vec2(0.37, 0.61);
		float high = smoothstep(0.3, 0.9, density(uv2, 3)) * 0.35 * smoothstep(0.0, 0.12, g.y);
		scat += tr * high * mix(I76_COL(cloud_color), I76_COL(horizon_color), 0.3);
		tr *= 1.0 - high;
	}
	return vec4(scat, tr);
}

void sky() {
	if (I76_VOL_PASS) {
		// the cube map's own reduced pass is not used: nothing to march
		vec4 v = AT_CUBEMAP_PASS ? vec4(0.0, 0.0, 0.0, 1.0) : march(EYEDIR, TIME, FRAGCOORD.xy);
		COLOR = v.rgb;
		ALPHA = v.a;
	} else if (AT_CUBEMAP_PASS) {
		// the radiance (ambient light and reflections) keeps the flat clouds
		COLOR = modern_sky(EYEDIR, TIME, true);
	} else {
		COLOR = modern_sky_with(EYEDIR, TIME, false, true, I76_VOL_COLOR);
	}
}
