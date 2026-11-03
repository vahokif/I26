// The modern look's distant terrain (HorizonTerrain.cs) and the outside ground near the camera (OutsideTerrain.cs): desert rock and sand
// on a baked polar mesh, lit by the mission's sun with a baked sun visibility
// (a march over the heights, COLOR.r) on top of Godot's own shadow map near
// the map, a warm fill from the sunlit ground on the faces turned from it
// (light()), and hazed with distance toward the modern sky's horizon colour in
// the view direction (the same gradient and sun glow sky_modern.gdshaderinc
// draws below the horizon), so that at the far clip it is the sky.
//
// Rock colour comes from the horizon panels by bearing (HorizonTerrain.Measure),
// broken into strata and varnish streaks; flat ground near the map takes the
// ground texture's colour, farther out the scrub along the panels' foot. Detail
// normals are value-noise octaves with analytic gradients, each fading out as
// it gets finer than a pixel.

uniform sampler2D rock_by_bearing : source_color, filter_linear, repeat_enable;
uniform vec3 rock_light : source_color = vec3(0.7, 0.5, 0.35);
uniform vec3 rock_dark : source_color = vec3(0.3, 0.18, 0.12);
uniform vec3 scrub_color : source_color = vec3(0.35, 0.3, 0.22);
uniform vec3 ground_color : source_color = vec3(0.5, 0.3, 0.2);
uniform float snow = 0.0;
uniform float base_height = 0.0;
uniform float map_radius = 2000.0;

// the modern sky's horizon (Main.ApplySky copies them from the sky material)
uniform vec3 horizon_color : source_color = vec3(0.7, 0.75, 0.8);
uniform vec3 sprite_dir = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the light
uniform float light_strength = 1.0;
uniform float haze_start = 2000.0;   // metres of clear air
uniform float haze_reach = 13000.0;  // then 1 - exp(-(d/reach)^2): 63% this far past the clear air
uniform float far_clip = 20000.0;    // wholly sky by here
uniform float haze_floor = 0.0;      // air this much hazier from the start (night)

// The heights come sunk as the panels sink (the cone through their foot for one reference eye per map, OutsideGround):
// nothing here moves with the camera.

// OutsideTerrain draws the outside ground's own samples near the camera with this shading (horizon_near.gdshader, the same
// without the ring's depth and masks, so the GPU can reject hidden fragments early): the ring gives way under them. R of
// near_mask is 1 for each 160 m chunk they cover (near_origin its corner, centre-relative game frame, near_cells chunks a side).
uniform sampler2D near_mask : filter_nearest, repeat_disable;
uniform vec2 near_origin = vec2(0.0);
uniform float near_cells = 0.0;      // 0: none
const float NEAR_CHUNK = 160.0;
#ifndef HORIZON_RING
// the chunks near the camera ease onto the ring's own surface (UV2.x, where UV2.y is 1) between these distances from the camera, so
// where they end the ring goes on at their height; a little over it, so that where the ring is drawn a little higher than the chunks'
// edge (its triangles against their points) no sky shows through the step
uniform float morph_start = 800.0;
uniform float morph_end = 1200.0;
const float MORPH_LIFT = 2.5;
// the chunks near the camera: the mission's ground texture over the flat ground's colour (its texels about its mean, ground_color),
// repeating every 40 m as the engine's does, so the near ground has grain; its mipmaps settle to the mean, the ring's own colour
uniform sampler2D ground_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_ground_tex = false;
#endif

// R: the lowest playable ground near each 10 m texel of the map's bounds (1e9 off the tiles; HorizonTerrain.FloorGrid): over the
// map the ring is drawn only where it is under that, as a backstop under the terrain's edge, never poking through the ground.
// G: 1 on a tile's outermost texels.
uniform float debug_tint = 0.0;   // I76_HORIZON_TINT=1: the ring drawn magenta (to see where it shows)
uniform sampler2D map_floor : filter_nearest, repeat_disable;
uniform vec2 map_origin = vec2(0.0);
uniform vec2 map_extent = vec2(0.0);   // 0: no floor

varying vec3 g_pos;    // centre-relative, game frame
varying vec3 g_nrm;
varying vec4 v_data;   // sun visibility, rockiness, style (0 peaks .. 1 mesas)
varying vec2 v_aux;    // height above the base, distance from the map
varying vec3 v_up;     // world up in view space, for light()

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
// one without sin, for cell indices in the thousands (Hoskins' hash12)
float hash_cell(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// value noise (-1..1) and its gradient
vec3 noised(vec2 x) {
	vec2 i = floor(x), f = fract(x);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	vec2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
	float a = hash(i), b = hash(i + vec2(1, 0)), c = hash(i + vec2(0, 1)), d = hash(i + vec2(1, 1));
	float k1 = b - a, k2 = c - a, k4 = a - b - c + d;
	return vec3(a + k1 * u.x + k2 * u.y + k4 * u.x * u.y, du * vec2(k1 + k4 * u.y, k2 + k4 * u.x)) * 2.0 - vec3(1.0, 0.0, 0.0);
}

void vertex() {
#ifndef HORIZON_RING
	if (UV2.y > 0.5) {
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		VERTEX.y = mix(VERTEX.y, UV2.x + MORPH_LIFT, smoothstep(morph_start, morph_end, length(wp.xz - CAMERA_POSITION_WORLD.xz)));
	}
#endif
	g_pos = vec3(VERTEX.x, VERTEX.y, -VERTEX.z);
	g_nrm = vec3(NORMAL.x, NORMAL.y, -NORMAL.z);
	v_data = COLOR;
	v_aux = UV;
}

void fragment() {
#ifdef HORIZON_RING
	// every path writes DEPTH (the backstop below moves it): a shader that writes it on one path only leaves it undefined
	// on the others, which some drivers take as near, drawing the whole ring over the map (#69)
	DEPTH = FRAGCOORD.z;
	if (near_cells > 0.0) {
		vec2 cell = (g_pos.xz - near_origin) / NEAR_CHUNK;
		if (cell.x >= 0.0 && cell.y >= 0.0 && cell.x < near_cells && cell.y < near_cells
			&& textureLod(near_mask, (floor(cell) + 0.5) / near_cells, 0.0).r > 0.5) discard;
	}
	if (map_extent.x > 0.0) {
		vec2 tuv = (g_pos.xz - map_origin) / map_extent;
		if (tuv.x >= 0.0 && tuv.y >= 0.0 && tuv.x < 1.0 && tuv.y < 1.0) {
			vec2 fl = textureLod(map_floor, tuv, 0.0).rg;
			if (g_pos.y > fl.r) {
				// over the outermost 10 m of a tile it stays as a backstop behind the terrain's edge (which the modern surface
				// can pull a few metres in from the tile's), at twice the distance, so that nothing of the ground stands behind it
				if (fl.g > 0.5) DEPTH = FRAGCOORD.z * 0.5; else discard;
			}
		}
	}
#endif
	float dist = length(VERTEX);
	float footprint = max(length(dFdx(g_pos)), length(dFdy(g_pos)));
	vec3 n = normalize(g_nrm);
	float rocky = v_data.g, style = v_data.b;
	float above = v_aux.x, from_map = v_aux.y;

	// ---- detail: a few octaves of noise, as bumps and as colour (OutsideTerrain's chunks near the camera down to 1.25 m) ----
	vec2 grad = vec2(0.0);
	float detail = 0.0, cell = 320.0, amp = 1.0;
#ifdef HORIZON_RING
	int octaves = 6;
#else
	int octaves = dist < 100.0 ? 9 : 6;   // the chunks near the camera: down to 1.25 m close by
#endif
	for (int i = 0; i < octaves; i++) {
		float keep = smoothstep(1.0, 4.0, cell / footprint);
		if (keep <= 0.0) break;
		float a = float(i) * 2.3999632;
		mat2 rot = mat2(vec2(cos(a), sin(a)), vec2(-sin(a), cos(a)));
		vec3 nd = noised(rot * g_pos.xz / cell + vec2(float(i) * 17.31, float(i) * 9.7));
		detail += nd.x * amp * keep;
		grad += (transpose(rot) * nd.yz) / cell * amp * keep * cell * 0.08;
		cell *= 0.5;
		amp *= 0.6;
	}
	float steep = 1.0 - n.y;
	// steeper ground, rougher bumps
	grad *= mix(0.6, 1.6, smoothstep(0.1, 0.6, steep));
	vec3 nb = normalize(n - vec3(grad.x, 0.0, grad.y) * (1.0 - abs(n.y) * 0.2));

	// ---- colour ----
	float bearing = fract(atan(g_pos.x, g_pos.z) / 6.2831853);
	vec3 rock = texture(rock_by_bearing, vec2(bearing, 0.5)).rgb;
	// strata: bands by height, wavering, in the panels' own light and dark
	float band = sin(g_pos.y * 0.11 + detail * 2.5) * 0.5 + 0.5;
	float band2 = sin(g_pos.y * 0.037 + detail * 1.3 + 1.7) * 0.5 + 0.5;
	rock = mix(rock, mix(rock_dark, rock_light, band2), 0.3);
	rock *= mix(0.85, 1.12, band * mix(0.4, 1.0, style));
	// desert varnish: dark streaks down the cliffs, in cells 4 m wide along the face (and 300 m deep across it,
	// so they never line up into spokes), settling to their mean darkening once finer than a pixel
	vec2 nh = n.xz / max(length(n.xz), 1e-4);
	vec2 cellv = floor(vec2(dot(g_pos.xz, vec2(-nh.y, nh.x)) / 4.0, dot(g_pos.xz, nh) / 300.0));
	float streak = mix(0.35, smoothstep(0.4, 0.9, hash_cell(cellv)), smoothstep(1.0, 3.0, 4.0 / footprint));
	rock = mix(rock, rock_dark * 0.7, streak * smoothstep(0.5, 0.8, steep) * 0.35);
	vec3 flat_col = mix(ground_color, mix(ground_color, scrub_color, 0.35), smoothstep(400.0, 4000.0, from_map));
	flat_col *= 0.9 + 0.12 * detail;
#ifndef HORIZON_RING
	if (has_ground_tex) flat_col *= mix(vec3(1.0), texture(ground_tex, g_pos.xz / 40.0).rgb / max(ground_color, vec3(0.02)), 1.0 - smoothstep(300.0, 900.0, dist));
#endif
	float r = clamp(max(rocky * 1.2, smoothstep(0.18, 0.45, steep)), 0.0, 1.0);
	// talus and sand collect where it flattens
	r *= smoothstep(0.05, 0.22, steep + rocky * 0.15 + detail * 0.06);
	vec3 albedo = mix(flat_col, rock, r);
	// snow on high, flatter ground, where the panels had it
	if (snow > 0.02) {
		float s = smoothstep(0.35, 0.6, rocky + detail * 0.1) * smoothstep(0.45, 0.75, nb.y) * clamp(snow * 6.0, 0.0, 1.0);
		albedo = mix(albedo, vec3(0.85, 0.87, 0.9), s);
	}

	// ---- haze toward the sky's horizon, in this direction ----
	vec3 view_w = normalize((INV_VIEW_MATRIX * vec4(VERTEX, 0.0)).xyz);
	float sun = max(dot(view_w, normalize(sprite_dir)), 0.0);
	vec3 haze_col = horizon_color + vec3(1.0, 0.85, 0.6) * (pow(sun, 8.0) * 0.18 + pow(sun, 64.0) * 0.35) * light_strength;
	float t = max(dist - haze_start, 0.0) / haze_reach;
	float f = clamp(1.0 - (1.0 - haze_floor) * exp(-t * t), 0.0, 1.0);
	f = max(f, smoothstep(far_clip * 0.75, far_clip * 0.98, dist));

#ifdef HORIZON_RING
	if (debug_tint > 0.0) { albedo = vec3(1.0, 0.0, 1.0); f = 0.0; }
#endif
	ALBEDO = albedo * (1.0 - f);
	EMISSION = haze_col * f;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
	vec3 nw = vec3(nb.x, nb.y, -nb.z);   // game -> Godot
	NORMAL = normalize((VIEW_MATRIX * vec4(nw, 0.0)).xyz);
	v_up = (VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz;
}

// Fill bounced off the sunlit desert floor. With the sky's ambient alone, the flanks turned from the sun were a
// flat olive-grey smear a few km out (t01, just past the map's wall): blue light on red rock, the haze's
// airlight on top, and none of the relief, the bumps only tilting against a light from everywhere. Rock in
// shade in the desert keeps a warm colour, lit by the sunlit ground below and in front of it: here a light of
// the ground's colour, low on the side away from the sun, wrapped, neither shadowed nor baked (the ground it
// comes from is in the sun), where the sun does not reach. `bounce` is a look, not a measured albedo: warm
// enough that shaded rock reads as rock beside the map's own shaded cliffs.
uniform float bounce = 3.0;

void light() {
	float ndl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
	float vis = LIGHT_IS_DIRECTIONAL ? v_data.r : 1.0;
	DIFFUSE_LIGHT += ndl * ATTENUATION * LIGHT_COLOR / PI * vis;
	if (LIGHT_IS_DIRECTIONAL) {
		float elev = dot(LIGHT, v_up);
		vec3 away = LIGHT - v_up * elev;
		vec3 from_ground = normalize(-away / max(length(away), 1e-4) - v_up * 0.35);
		float wrap = clamp(dot(NORMAL, from_ground) * 0.6 + 0.4, 0.0, 1.0);
		// only where the sun does not already reach
		DIFFUSE_LIGHT += wrap * (1.0 - ndl * vis) * max(elev, 0.0) * ground_color * bounce * LIGHT_COLOR / PI;
	}
}
