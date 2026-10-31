// The modern look's procedural plants (World/Vegetation.cs, docs/modern-vegetation.md): one material for every
// species, the painted atlas (FoliageAtlas) alpha-tested, coloured by the mesh. Per instance (INSTANCE_CUSTOM):
// x stiffness (0 limp, 1 rigid), y sway phase, z the plant's height in metres, w a colour variation in -1..1.
// Per vertex: UV2.x the sway weight (0 at the ground, 1 at the top), UV2.y 1 on foliage cards (0.6 on thin grass blades:
// translucent, less flutter), COLOR.a the baked ambient occlusion (PlantShading: dark inside the crown and under it),
// CUSTOM0 on thin blades their side (PlantMesh.Side: the offset to this vertex from the blade's middle line, and the
// blade's base half-width in 0.1 mm plus a random 0..1).
// The rolling tumbleweeds (World/Tumbleweeds.cs) set `rolling`: INSTANCE_CUSTOM.x is then how much of the plant is drawn
// (fading in, a burst piece fading out, by the same dither), and nothing sways, parts round a car or carries dust at its foot.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
render_mode cull_disabled, world_vertex_coords;

uniform sampler2D atlas : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
// the mission's light on the land: a small pull toward its ground colour, and dust on the lowest parts
uniform vec3 tint = vec3(1.0);
uniform vec3 dust = vec3(0.6, 0.45, 0.35);
// how much colour the plants keep: the ground's own saturation, so a grey land (T04's dream) has grey plants
uniform float saturation = 1.0;
// where plants fade out, from the main camera (MAIN_CAM_INV_VIEW_MATRIX: the shadow pass's camera is the light's)
uniform float fade_start = 200.0;
uniform float fade_end = 250.0;
// the detail tiers cross-fade (TierBands): this tier's fade-in band and fade-out band, in ground metres from the camera
// (start, end; none when end <= start). In its fade-in a plant is drawn where the dither passes it, in its fade-out
// where it does not pass the complement, so two tiers about a boundary together cover every pixel once.
uniform vec2 tier_in = vec2(0.0);
uniform vec2 tier_out = vec2(0.0);
uniform vec2 wind_dir = vec2(0.8, 0.6);
uniform float wind = 1.0;
uniform bool rolling = false;
// the cars' positions (World/CabClip.cs: x, y, z, and the radius a plant is parted from), 16 at most
global uniform int i76_car_count;
global uniform sampler2D i76_cars : filter_nearest;
// how much of the sun's light the baked occlusion takes away too (all of the sky's)
uniform float ao_light = 0.6;
// a thin blade is kept at least this many pixels wide; as it is widened, its tuft's other blades thin out to keep the tuft's mass
uniform float min_px = 0.8;
// I76_VEG_DEBUG: 1 the light alone, on grey plants (against the terrain's I76_TERRAIN_DEBUG=2); 2 the baked occlusion
uniform int debug_view = 0;

varying float v_fade;
varying float v_sway;
varying float v_vary;
varying float v_tin;
varying float v_tout;

void vertex() {
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
	COLOR = CUSTOM1;
#endif
	vec3 origin = MODEL_MATRIX[3].xyz;
	v_fade = 1.0 - smoothstep(fade_start, fade_end, distance(origin, MAIN_CAM_INV_VIEW_MATRIX[3].xyz));
	vec2 dh = origin.xz - MAIN_CAM_INV_VIEW_MATRIX[3].xz;
	float gd = length(dh);
	v_tin = tier_in.y > tier_in.x ? smoothstep(tier_in.x, tier_in.y, gd) : 1.0;
	v_tout = tier_out.y > tier_out.x ? smoothstep(tier_out.x, tier_out.y, gd) : 0.0;
	float stiff = rolling ? 1.0 : INSTANCE_CUSTOM.x;
	if (rolling) v_fade *= INSTANCE_CUSTOM.x;
	float phase = INSTANCE_CUSTOM.y * 6.2832;
	float height = max(INSTANCE_CUSTOM.z, 0.2);
	v_vary = INSTANCE_CUSTOM.w;
	float s = UV2.x;
	v_sway = rolling ? 1.0 : s;
	// grass blades far off: hair-thin blades would break up into sub-pixel sparkle, so each is widened to min_px pixels
	// and, to keep the tuft's coverage, only the share of blades its true width pays for is kept (the others narrowed
	// away by their random; widening and thinning grow together with distance, so none pops)
	if (CUSTOM0.w > 0.0) {
		vec3 off = mat3(MODEL_MATRIX) * CUSTOM0.xyz;
		float hw = length(off);
		float px = 2.0 * distance(VERTEX, MAIN_CAM_INV_VIEW_MATRIX[3].xyz) / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y);
		float want = 0.5 * min_px * px;
		// the blade's mean half-width (it tapers to its tip) against the minimum
		float q = floor(CUSTOM0.w) * 1e-4 * 0.7 * length(MODEL_MATRIX[0].xyz) / want;
		float id = fract(CUSTOM0.w);
		float alive = smoothstep(id - 0.06, id + 0.06, q);
		if (hw > 1e-6) VERTEX += off * ((max(hw, want) * alive - hw) / hw);
	}
	// a slow gust front crossing the land, and each plant's own swing on it
	float gust = 0.55 + 0.45 * sin(TIME * 0.45 - dot(origin.xz, wind_dir) * 0.035);
	float swing = 0.6 * sin(TIME * 1.6 + phase + dot(origin.xz, vec2(0.11, 0.07))) + 0.4 * sin(TIME * 2.7 + phase * 1.7);
	float amp = (1.0 - stiff) * s * s * height * 0.035 * wind * gust;
	vec2 push = normalize(wind_dir) * (amp * (0.6 + 0.6 * swing));
	// the leaves flutter a little on their own
	float flutter = UV2.y * (1.0 - stiff) * s * 0.025 * sin(TIME * 7.0 + dot(VERTEX, vec3(3.1, 2.3, 4.7)));
	VERTEX.xz += push + vec2(flutter);
	VERTEX.y -= length(push) * 0.3 * s;
	// a car driving through parts the plant: its crown is pushed away from the car and laid down, the stiff ones (cacti, yucca) far less
	float give = rolling ? 0.0 : (1.0 - 0.85 * stiff) * s;
	for (int i = 0; i < i76_car_count; i++) {
		vec4 c = texelFetch(i76_cars, ivec2(i, 0), 0);
		vec2 d = origin.xz - c.xz;
		float dist = length(d);
		float t = (1.0 - smoothstep(c.w * 0.55, c.w * 1.7, dist)) * (1.0 - smoothstep(2.5, 4.0, abs(origin.y - c.y)));
		VERTEX.xz += d / max(dist, 0.05) * (t * give * height * 0.7);
		VERTEX.y -= t * give * height * 0.6;
	}
}

// a 4x4 ordered dither: the fade thins plants out pixel by pixel, never pops them
float bayer(vec2 p) {
	ivec2 q = ivec2(mod(p, 4.0));
	int i = q.x + q.y * 4;
	int m[16] = int[](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
	return (float(m[i]) + 0.5) / 16.0;
}

void fragment() {
	CAB_CLIP
	float dither = bayer(FRAGCOORD.xy);
	if (v_fade < 1.0 && v_fade <= dither) discard;
	// the tier cross-fade: the incoming tier keeps the pixels the outgoing one gives up
	if (dither < 1.0 - v_tin || dither >= 1.0 - v_tout) discard;
	vec4 tex = texture(atlas, UV);
	vec3 col = tex.rgb * COLOR.rgb * tint * (1.0 + 0.1 * v_vary);
	// the base of a plant carries the ground's dust
	col = mix(col, col * dust * 1.6, (1.0 - smoothstep(0.0, 0.18, v_sway)) * 0.45);
	col = mix(vec3(dot(col, vec3(0.2126, 0.7152, 0.0722))), col, saturation);
	ALBEDO = col;
	ALPHA = tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	// the authored normals either side (Godot turns them round on back faces): foliage and blades shade as their crown or tuft
	if (!FRONT_FACING) NORMAL = -NORMAL;
	ROUGHNESS = 0.82;
	SPECULAR = 0.25;
	// the baked occlusion (PlantShading): the sky's light, and much of the sun's (the shadow map reaches few plants), is lost inside the crown and under it
	float ao = COLOR.a;
	AO = ao;
	AO_LIGHT_AFFECT = ao_light;
	// light through the leaves and the thin blades
	BACKLIGHT = vec3(0.18, 0.2, 0.1) * UV2.y * ao;
	if (debug_view == 1) ALBEDO = vec3(0.35);
	if (debug_view == 2) ALBEDO = vec3(ao);
}
