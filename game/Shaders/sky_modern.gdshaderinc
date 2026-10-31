// The modern sky as a function of the view direction (Godot frame), shared by
// sky_modern.gdshader and the modern look's fog (sky_fog.gdshader), which fades
// distant ground into exactly the sky behind it.
// The Compatibility renderer (OpenGL, docs/compatibility-renderer.md) does not sRGB-decode an array texture's
// source_color, nor a sky shader's textures, and its sky pass writes display values without the linear-to-sRGB
// encode the other renderers' do: the sky shader there (I76_RAW_SKY) decodes by hand and encodes its colour at the end.
vec3 i76_srgb_to_linear(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }
vec3 i76_linear_to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
vec4 i76_array_tex(vec4 c) { return vec4(i76_srgb_to_linear(c.rgb), c.a); }
#else
vec4 i76_array_tex(vec4 c) { return c; }
#endif
#ifdef I76_RAW_SKY
vec4 i76_sky_tex(vec4 c) { return vec4(i76_srgb_to_linear(c.rgb), c.a); }
#else
vec4 i76_sky_tex(vec4 c) { return c; }
#endif

#ifdef I76_RAW_SKY
#define I76_COL(c) (cubemap ? (c) : i76_srgb_to_linear(c))
#else
#define I76_COL(c) (c)
#endif
uniform sampler2D clouds : source_color, filter_linear, repeat_enable;
uniform sampler2DArray horizon : source_color, filter_linear_mipmap, repeat_disable;
uniform bool has_horizon = false;
uniform bool ground_haze = false;   // the 3D horizon (HorizonTerrain): below the horizon the haze it fades into
// with the 3D horizon, the panels still light the scene: drawn into the radiance cubemap only (ambient and
// reflections), so the lower half of the sky light stays their sunlit desert foot and not the pale haze
uniform bool horizon_light = false;
uniform sampler2D sprite : source_color, filter_linear, repeat_disable;
uniform bool has_sprite = false;
uniform vec3 sprite_dir = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the light
uniform float sprite_radius = 0.05;
uniform bool procedural_sun = false;   // by day: a drawn sun instead of the sprite
uniform float sun_radius = 0.012;      // radians (the real sun is 0.0047; a little larger reads better at game FOVs)
uniform float sky_gain = 1.0;          // the sky behind the sun or moon scaled: at night the Filmic tonemap's toe would crush the original's navy
uniform bool procedural_moon = false;  // by night: a drawn moon (moon_tex, an equirectangular map of its surface) instead of the sprite
uniform sampler2D moon_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform float moon_radius = 0.05;     // radians (the real moon is 0.0045; drawn larger, about two thirds of the mission sprite's disc)
// the north celestial pole as seen from the trip (west Texas and New Mexico, about 32 degrees north), Godot frame: the game's +Z is north
// (the compass), Godot's -Z. The moon's north points at it, so a moon rising in the east leans its north to the left, as it does there
uniform vec3 moon_north = vec3(0.0, 0.5299, -0.8480);
uniform float scroll_per_second = 0.02997;
uniform float radius = 6000.0;
uniform float drop = 5400.0;
uniform float horizon_height = 1170.6;
uniform float horizon_foot_deg = -1.0;

// measured from the mission's texture (MissionBuilder.SkyPalette)
uniform vec3 zenith_color : source_color = vec3(0.25, 0.42, 0.75);
uniform vec3 horizon_color : source_color = vec3(0.7, 0.75, 0.8);
uniform vec3 cloud_color : source_color = vec3(1.0);
uniform vec3 cloud_shade : source_color = vec3(0.55, 0.58, 0.65);
uniform float sky_lum = 0.15;
uniform float cloud_lum = 0.8;
uniform float detail = 0.35;      // how much the noise reshapes the texture's clouds
uniform float light_strength = 1.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p, int octaves) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < octaves; i++) { s += a * vnoise(p); p = p * 2.03 + vec2(17.3, 9.1); a *= 0.5; }
	return s;
}

// the texture's cloudiness at a dome coordinate, reshaped by noise
float density(vec2 uv, int octaves) {
	// a small blur: the texture's texels span tens of metres on the dome, and the ramp would sharpen their edges
	const float r = 0.006;
	vec3 c = i76_sky_tex(texture(clouds, uv)).rgb * 0.4 + (i76_sky_tex(texture(clouds, uv + vec2(r, 0))).rgb + i76_sky_tex(texture(clouds, uv - vec2(r, 0))).rgb
		+ i76_sky_tex(texture(clouds, uv + vec2(0, r))).rgb + i76_sky_tex(texture(clouds, uv - vec2(0, r))).rgb) * 0.15;
	float span = cloud_lum - sky_lum;
	float base = smoothstep(sky_lum - span * 0.1, cloud_lum + span * 0.15, dot(c, vec3(0.299, 0.587, 0.114)));
	// billows: large lumps inside, fine erosion at the edges
	float n = fbm(uv * 5.0, octaves);
	float e = fbm(uv * 17.0 + n, octaves > 1 ? octaves - 1 : 1);
	float edge = 1.0 - abs(base * 2.0 - 1.0);
	return clamp(base + (n - 0.5) * detail * 1.6 + (e - 0.5) * detail * 1.4 * edge, 0.0, 1.0);
}

vec3 modern_sky(vec3 eyedir, float time, bool cubemap) {
	vec3 d = normalize(eyedir);
	vec3 g = vec3(d.x, d.y, -d.z);    // Godot -> game: mirror Z
	vec3 s = normalize(sprite_dir);
	float sun = max(dot(d, s), 0.0);

	// the sky behind: the texture's blue overhead, paler toward the horizon, warmer round the sun
	float up = clamp(g.y, 0.0, 1.0);
	vec3 col = mix(I76_COL(horizon_color), I76_COL(zenith_color), pow(up, 0.55));
	// round the moon a faint cold glow only: the sun's wide one would grey the whole night sky
	col += (procedural_moon ? vec3(0.72, 0.78, 0.9) * (pow(sun, 64.0) * 0.06) : vec3(1.0, 0.85, 0.6) * (pow(sun, 8.0) * 0.18 + pow(sun, 64.0) * 0.35)) * light_strength;

	// what reaches the eye from the sun or moon through the clouds and the haze, added once the sky is done
	float transmit = 1.0;
	if (g.y > 0.0) {
		// the engine's dome projection
		float b = g.y * drop;
		float t = -b + sqrt(b * b - (drop * drop - radius * radius));
		vec2 xz = (t * g).xz;
		float off = time * scroll_per_second;
		vec2 uv = (xz / radius + 0.5) * 4.0 + vec2(off);
		int oct = cubemap ? 2 : 5;
		float den = density(uv, oct);
		// light from the sun's side: cloud between here and the sun shades this point
		vec2 toward = normalize(vec2(s.x, -s.z) + vec2(1e-4)) * 0.035;
		float acc = 0.0;
		for (int i = 1; i <= 3; i++) acc += density(uv + toward * float(i), cubemap ? 1 : 3);
		float lit = exp(-acc * 0.75);
		float thick = smoothstep(0.2, 0.95, den);
		vec3 cl = mix(I76_COL(cloud_shade) * 0.9, I76_COL(cloud_color) * 1.08, lit);
		cl = mix(cl, I76_COL(cloud_shade) * 0.8, thick * thick * 0.35);
		cl += (procedural_moon ? vec3(0.75, 0.82, 1.0) : vec3(1.0, 0.92, 0.78)) * pow(sun, 10.0) * (1.0 - thick) * 0.7 * light_strength;   // silver lining (the moon's cold)
		float cover = smoothstep(0.03, 0.75, den);
		// a fainter, higher layer drifting slower, for depth
		vec2 uv2 = (xz / radius + 0.5) * 2.0 + vec2(off * 0.4, -off * 0.25) + vec2(0.37, 0.61);
		float high = smoothstep(0.3, 0.9, density(uv2, cubemap ? 1 : 3)) * 0.35;
		col = mix(col, mix(I76_COL(cloud_color), I76_COL(horizon_color), 0.3), high);
		col = mix(col, cl, cover);
		// clouds thin into the haze near the horizon
		col = mix(I76_COL(horizon_color), col, smoothstep(0.0, 0.12, g.y));
		transmit = (1.0 - cover * 0.92) * (1.0 - high * 0.5);
		// a cloud in front of the sun glows round it (forward scattering), brightest where it is thin
		if (procedural_sun) col += vec3(1.0, 0.95, 0.85) * (pow(sun, 300.0) * 1.6 + pow(sun, 40.0) * 0.25) * cover * (1.0 - thick * 0.5) * light_strength;
	}
	col *= sky_gain;
	// low down, the long path through the haze dims it (the disc itself reddens below)
	float air = smoothstep(-0.02, 0.15, g.y);
	transmit *= mix(0.25, 1.0, air);

	if (procedural_sun) {
		float c = dot(d, s);
		float a = acos(clamp(c, -1.0, 1.0));
		vec3 tint = mix(vec3(1.0, 0.55, 0.28), vec3(1.0, 0.96, 0.9), air);
		// a tight glare round it, in the cube map too (it lights the scene as the sun's own glow would)
		col += tint * (pow(max(c, 0.0), 900.0) * 0.8 + pow(max(c, 0.0), 6000.0) * 2.0) * light_strength * transmit;
		if (!cubemap && a < sun_radius * 1.2) {
			// the disc: limb-darkened, its edge anti-aliased over a pixel's width
			float r = a / sun_radius;
			float edge = 1.0 - smoothstep(1.0 - fwidth(r) * 1.5, 1.0, r);
			float limb = 0.6 + 0.4 * sqrt(max(0.0, 1.0 - r * r));
			col += tint * 14.0 * limb * edge * light_strength * transmit;
		}
	} else if (procedural_moon) {
		float c = dot(d, s);
		float a = acos(clamp(c, -1.0, 1.0));
		// low down the haze warms it, as it reddens the sun
		vec3 tint = mix(vec3(1.0, 0.78, 0.6), vec3(1.0), air);
		// a faint cold halo in the night air, in the cube map too
		col += vec3(0.7, 0.8, 1.0) * (pow(max(c, 0.0), 4000.0) * 0.12 + pow(max(c, 0.0), 300.0) * 0.035) * transmit;
		if (!cubemap && a < moon_radius * 1.2) {
			// the near side as a sphere facing the eye, the map's longitude 0 at the disc's centre and its north toward the celestial pole
			// (seen from the northern hemisphere: Mare Crisium to the right, Tycho below); right is the disc's west side
			vec3 axis = abs(dot(normalize(moon_north), s)) > 0.999 ? vec3(1.0, 0.0, 0.0) : moon_north;
			vec3 right = normalize(cross(axis, s));
			vec3 upv = cross(s, right);
			vec2 p = vec2(dot(d, right), dot(d, upv)) / sin(moon_radius);
			float r = length(p);
			float edge = 1.0 - smoothstep(1.0 - fwidth(r) * 1.5, 1.0, r);
			float z = sqrt(max(0.0, 1.0 - r * r));
			vec2 muv = vec2(0.5 + atan(-p.x, z) / 6.2831853, 0.5 - asin(clamp(p.y, -1.0, 1.0)) / 3.1415927);
			vec3 alb = textureGrad(moon_tex, muv, dFdx(muv), dFdy(muv)).rgb;
			// a full moon: lit face-on, barely darker toward the limb (the regolith scatters back)
			float limb = 0.82 + 0.18 * z;
			col = mix(col, alb * 2.6 * limb * tint, edge * transmit);
		}
	} else if (has_sprite) {
		float c = dot(d, s);
		if (c > cos(sprite_radius * 1.5)) {
			vec3 right = normalize(cross(abs(s.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0), s));
			vec3 upv = cross(s, right);
			vec2 p = vec2(dot(d, right), dot(d, upv)) / tan(sprite_radius);
			vec2 suv = p * 0.5 + 0.5;
			if (all(greaterThanEqual(suv, vec2(0.0))) && all(lessThanEqual(suv, vec2(1.0)))) {
				vec4 sp = i76_sky_tex(texture(sprite, suv));
				col = mix(col, sp.rgb * (1.0 + light_strength), sp.a * transmit);
			}
		}
	}

	if (has_horizon || (horizon_light && cubemap)) {
		float horiz = length(g.xz);
		if (horiz > 1e-4) {
			float h = radius * g.y / horiz;
			float foot = radius * tan(radians(horizon_foot_deg));
			float v = (h - foot) / horizon_height;
			float phi = atan(g.z, g.x);
			float k = mod((1.5707963 - phi) / 0.39269908, 16.0);
			float u = 1.0 / 128.0 + fract(k) * (1.0 - 2.0 / 128.0);
			if (v >= 0.0 && v <= 1.0) {
				vec4 hz = i76_array_tex(textureLod(horizon, vec3(u, v, floor(k)), 0.0));
				col = mix(col, hz.rgb, hz.a);
			} else if (v < 0.0) {
				// below the panels' foot their bottom row carries on down, blurred sideways more the further it
				// goes (seen through the distance fog over far ground, sky_fog.gdshader, a sharp row would streak;
				// mipmaps would average in the clear sky above the mountains)
				float spread = clamp(-v * 2.0, 0.0, 0.6);
				vec4 acc = vec4(0.0);
				for (int i = 0; i < 8; i++) {
					float kk = mod(k + (float(i) - 3.5) / 3.5 * spread, 16.0);
					vec4 t = i76_array_tex(textureLod(horizon, vec3(1.0 / 128.0 + fract(kk) * (1.0 - 2.0 / 128.0), 0.002, floor(kk)), 0.0));
					acc += vec4(t.rgb * t.a, t.a);
				}
				col = mix(col, acc.rgb / max(acc.a, 1e-4), acc.a / 8.0);
			}
		}
	} else if (g.y < 0.0 && !ground_haze) {
		col = I76_COL(horizon_color) * 0.8;
	}
	return col;
}
