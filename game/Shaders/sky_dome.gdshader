// The engine's sky, evaluated per pixel:
//
// - The dome: a sphere of radius 6000
//   centred 5400 below the eye, textured by a top-down projection
//   u = (x/6000 + 0.5)*4, v = (z/6000 + 0.5)*4 in the game frame, both
//   scrolled. The engine scrolls 1/(1001 - 2^-7) per *frame* (a frame-rate
//   bug); here it is per second at what that gives at 30 fps.
// - The horizon: 16 panels on a radius-6000
//   ring round the eye, starting at game +Z and turning toward +X, each
//   half a chord tall, texture row 0 at the foot (the art is stored bottom
//   up), u inset by 1/128. The ring is centred on the eye in x and z only:
//   its foot stands at game height -((6000 - 0.8F)/F)*camY, F the far clip
//   (600) and camY the eye's game height, so it meets the
//   ground's far edge only for an eye near height 0; over high ground (M04's
//   plain at 120) it sinks below the horizon and the terrain hides it.
// - The sun or moon sprite at the light direction.
// - The Software renderer: the 1997 CD build's horizon band in place of the
//   panels (it had none; drawn in
//   mode 9): a band from the line where the lowest drawn ground
//   meets the far clip up to where a plane 130 above the eye meets it, both
//   lines straight across the screen, shaded through the .lum rows of palette
//   0xef, row 0 at the foot to row 31 at the top (SoftwarePalette.GradientRow);
//   on the stock palettes its lowest fifth steps through the brighter sky
//   entries and the rest is 0xef. Below the foot its row 0 carries on down.
shader_type sky;

uniform sampler2D clouds : source_color, filter_linear, repeat_enable;
uniform sampler2DArray horizon : source_color, filter_linear_mipmap, repeat_disable;
uniform bool has_horizon = false;
uniform sampler2D sprite : source_color, filter_linear, repeat_disable;
uniform bool has_sprite = false;
uniform vec3 sprite_dir = vec3(0.0, 1.0, 0.0);   // Godot frame, toward the light
uniform float sprite_radius = 0.05;               // radians
uniform float scroll_per_second = 0.02997;
uniform float radius = 6000.0;
uniform float drop = 5400.0;
uniform float horizon_height = 1170.6;
uniform float far_clip = 600.0;
// Far draw distance (the 1997 look): the ground runs on past the clip to where the map's data ends, so the ring's foot
// meets that end, bearing by bearing: RG = the end's game x and z, B = the ground's game height there, A = 1 where it
// lies past the clip; 360 bins from game +X toward +Z, as seen from where they were measured
uniform bool has_edge = false;
uniform sampler2D edge : filter_linear, repeat_enable;
uniform float origin_y = 0.0;   // the floating origin's game height: game y = Godot y + origin_y
uniform vec2 origin_xz = vec2(0.0);   // and its game x and z
uniform float exposure = 1.0;
// the Software renderer (software.gdshaderinc): its clouds and horizon point-sampled
global uniform bool i76_software;
// and its horizon band (SoftwareLook): the 32 row colours, the camera's forward (Godot), the lowest ground's game height
uniform bool software_band = false;
uniform sampler2D band : source_color, filter_nearest, repeat_disable;
uniform vec3 view_forward = vec3(0.0, 0.0, -1.0);
uniform float band_ground = 0.0;
uniform float band_top = 130.0;
// the edge's smoothing: the bearings within EDGE_TAPS * EDGE_STEP of the view's (3 degrees)
const int EDGE_TAPS = 6;
const float EDGE_STEP = 0.5 / 360.0;
vec4 point_fetch(sampler2D s, vec2 uv) {
	ivec2 sz = textureSize(s, 0);
	ivec2 p = ivec2(floor(uv * vec2(sz)));
	return texelFetch(s, ((p % sz) + sz) % sz, 0);
}

void sky() {
	vec3 d = normalize(EYEDIR);
	vec3 g = vec3(d.x, d.y, -d.z);    // Godot -> game: mirror Z

	// dome
	float b = g.y * drop;
	float t = -b + sqrt(b * b - (drop * drop - radius * radius));
	vec2 xz = (t * g).xz;
	float off = TIME * scroll_per_second;
	vec2 cuv = (xz / radius + 0.5) * 4.0 + vec2(off);
	vec3 col = i76_software ? point_fetch(clouds, cuv).rgb : texture(clouds, cuv).rgb;

	// the software band: s is the direction's height over its view depth, linear across the screen, as the original's
	// lines are (drawn before the sun or moon, as there); a camera looking well away from view_forward (a mirror) takes the level view along its own bearing
	if (software_band) {
		float depth = dot(d, normalize(view_forward));
		float s = depth > 0.2 ? d.y / depth : d.y / max(length(d.xz), 1e-4);
		float cam_y = POSITION.y + origin_y;
		float foot = (band_ground - cam_y) / far_clip, top = band_top / far_clip;
		if (s <= top) {
			float t = top > foot ? clamp((top - s) / (top - foot), 0.0, 1.0) : 1.0;
			int row = clamp(int((1.0 - t) * 31.0), 0, 31);
			col = texelFetch(band, ivec2(row, 0), 0).rgb;
		}
	}

	// sun or moon
	if (has_sprite) {
		vec3 s = normalize(sprite_dir);
		float c = dot(d, s);
		if (c > cos(sprite_radius * 1.5)) {
			vec3 right = normalize(cross(abs(s.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0), s));
			vec3 up = cross(s, right);
			vec2 p = vec2(dot(d, right), dot(d, up)) / tan(sprite_radius);
			vec2 uv = p * 0.5 + 0.5;
			if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
				vec4 sp = texture(sprite, uv);
				col = mix(col, sp.rgb, sp.a);
			}
		}
	}

	// horizon
	if (has_horizon && !software_band) {
		float horiz = length(g.xz);
		if (horiz > 1e-4) {
			float h = radius * g.y / horiz;                   // height on the ring, eye level 0
			float cam_y = POSITION.y + origin_y;
			float foot = -((radius - 0.8 * far_clip) / far_clip) * cam_y - cam_y;   // relative to the eye
			if (has_edge) {
				// the same formula for the end's distance (from the eye as it is now, so the foot moves with it rather than
				// jumping when the ends are measured again) and height; the ends filtered between bins, and the lowest foot
				// of the bearings round this one, so the panels run smoothly along the ground's end and never stand above it
				vec2 eye = vec2(POSITION.x, -POSITION.z) + origin_xz;
				float u0 = atan(g.z, g.x) / 6.2831853;
				float lowest = 1e9;
				for (int i = -EDGE_TAPS; i <= EDGE_TAPS; i++) {
					vec4 e = texture(edge, vec2(u0 + float(i) * EDGE_STEP, 0.5));
					float de = length(e.rg - eye);
					lowest = min(lowest, e.a > 0.5 && de > far_clip ? -(cam_y - e.b) * (radius + 0.2 * de) / de : foot);
				}
				foot = lowest;
			}
			float v = (h - foot) / horizon_height;
			if (v >= 0.0 && v <= 1.0) {
				float phi = atan(g.z, g.x);                   // game azimuth
				float k = mod((1.5707963 - phi) / 0.39269908, 16.0);
				float panel = floor(k);
				float u = 1.0 / 128.0 + fract(k) * (1.0 - 2.0 / 128.0);
				vec4 hz = textureLod(horizon, vec3(u, v, panel), 0.0);   // always magnified; mip selection breaks at panel seams
				col = mix(col, hz.rgb, hz.a);
			} else if (v < 0.0) {
				// below the ring's foot: carry its bottom row down, so no sky shows under it
				float phi = atan(g.z, g.x);
				float k = mod((1.5707963 - phi) / 0.39269908, 16.0);
				vec4 hz = textureLod(horizon, vec3(1.0 / 128.0 + fract(k) * (1.0 - 2.0 / 128.0), 0.002, floor(k)), 0.0);
				col = mix(col, hz.rgb, hz.a);
			}
		}
	}
	COLOR = col * exposure;
}
