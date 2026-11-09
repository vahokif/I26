// Scenery and vehicles, included by the object_* shaders with their cull mode.
//
// Modern (no FAITHFUL): lit by Godot.
// 1997 (FAITHFUL): the engine's per-vertex object light (day presets):
//   light = min(clamp(z*fade_a + fade_b, 0.75, 1) * max(0, n.L) * intensity + ambient, 1)
// with n the outward normal (the GEO's inward one negated), through the
// mission's light ramp like the terrain (a face turned from the sun sits at
// the ramp's floor, never black). That colour is the albedo, and light() passes it as it is:
// the original's shadows are flat drop-shadow quads on the ground, so no object is ever darkened by one — not
// by another's, and not by its own (the environment adds no ambient in that
// look; the terrain takes the sun's shadow). TRANSLUCENT faces blend at `opacity`.

global uniform vec3 i76_light_dir;
global uniform float i76_light_intensity;
global uniform float i76_ambient;
global uniform vec2 i76_fade;
// the engine's light ramp (the mission's .act and .lum): a vertex lit `light` takes LUMA[round(31·light)]
#include "res://Shaders/ramp.gdshaderinc"
// a night preset (the light's other branch): no view-depth fade on the moon's term, and the cars' lights
// (NightLights.cs: row 0 position and exponent, row 1 the nose and 1/range², global space)
global uniform bool i76_night;
global uniform int i76_night_light_count;
global uniform sampler2D i76_night_lights : filter_nearest;
#include "res://Shaders/software.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform sampler2D albedo_cockpit : source_color, filter_linear_mipmap, repeat_disable;   // the cockpit art; clamped, so a face edge sampled a hair past 1.0 (MSAA) never wraps to the far row (the steering wheel quad drew its rim as a 1px line)
// The cockpit's art as drawn. The game's own low-resolution art (the 3D card takes no texture over 256 texels a side, so
// none of it is larger) is point-sampled from its full-size level, its pixels kept as the original's software renderer
// shows them (its 3D card filtered them): the dials' lettering stays sharp, where a mipmap level greyed it whenever the
// dash was smaller on screen than its texels. Larger art (a high-resolution replacement) is filtered, bilinear with its mipmaps.
vec4 i76_cockpit(vec2 uv) {
	ivec2 sz = textureSize(albedo_cockpit, 0);
	if (max(sz.x, sz.y) > 256) return texture(albedo_cockpit, uv);
	return texelFetch(albedo_cockpit, clamp(ivec2(floor(uv * vec2(sz))), ivec2(0), sz - 1), 0);
}
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform vec4 albedo_color_software : source_color = vec4(0.0);   // with alpha 1, a flat face's colour under the Software renderer, where it is not albedo_color's nearest entry (the gauge needles: the palette entry the original's software renderer draws)
uniform bool use_texture = false;
uniform bool alpha_key = false;
uniform bool round_key = false;   // the face is a disc inscribed in its square (a wheel): edged by an analytic circle, not the texture's alpha
uniform float opacity = 0.6;
uniform float shadow_dark = 0.0;   // the original's shadows were pure black
uniform bool prelit = false;       // the cockpit's art: drawn as painted, no light, no shadow
uniform bool unlit_1997 = false;   // the 1997 look: a face the engine draws at full brightness (FaceLight: lamps, lit windows, translucent faces)
// material maps (modern look): roughness, metallic, clear coat, specular; a car's (CarMaterial) with a tangent-space
// normal, scenery's and props' (PropMaterial) without
uniform bool has_maps = false;
uniform bool has_normal = false;
uniform bool car_paint = false;    // a car without maps: clear-coated paint all over
uniform bool wheel_face = false;   // with car_paint: a wheel's face, where the tyre's rubber is matte and only the hubcap keeps the shine
uniform bool normals_debug = false;   // I76_CAR_NORMALS_VIEW=1: the car's world-space shading normal as colour, unlit
uniform bool tread = false;        // the modern look's round wheel's tread band: rubber with a tread pattern (UV: u = blocks round the tyre, v = 0..1 across it; v > 1.5 plain rubber)
// a face without maps (a flat colour, the cockpit's): one material over it
uniform float base_roughness = 0.9;
uniform float base_metallic = 0.0;
uniform float base_specular = 0.3;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap_anisotropic;
// A respray: the car's custom paint colours, per car (a mesh instance's), and the scheme's paint mask,
// per material. paint_a: the primary paint's colour (sRGB) and, in alpha, 0 for none else 1 + the finish (1 gloss, 2 metallic, 3 matte);
// paint_b likewise for the secondary. paint_mask: red the primary weight, green the secondary's, same size as the albedo. With no colour
// (alpha 0, every car by default) nothing below runs: the texel is the texture's own.
instance uniform vec4 paint_a = vec4(0.0);
instance uniform vec4 paint_b = vec4(0.0);
uniform sampler2D paint_mask : filter_linear_mipmap_anisotropic;
uniform bool has_paint_mask = false;
uniform vec2 paint_luma = vec2(0.5);   // the mean luma of the scheme's primary and secondary paint

uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic;

varying vec2 v_intensity;
// a camera-facing card (ScenerySpawner): the engine draws it turned toward the view but lights it
// by its node's own orientation (the light is handed the node's world matrix, not the turn), so the 1997 look
// takes the sun's term with that rotation's columns; zero (every other object): MODEL_MATRIX's
instance uniform vec3 card_x = vec3(0.0);
instance uniform vec3 card_y = vec3(0.0);
instance uniform vec3 card_z = vec3(0.0);

vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 to_linear(vec3 c) { return mix(c / 12.92, pow(max((c + 0.055) / 1.055, vec3(0.0)), vec3(2.4)), step(vec3(0.04045), c)); }

#ifndef FAITHFUL
// A burnt-out car (WreckFire sets it on the wreck's mesh instances; 0 everywhere else): x how charred, 0..1;
// y its heat: 1 just after the death (the fire inside glowing through yellow-orange cracks), cooling through
// dull red embers to 0, plain char. One instance slot. The 1997 look does not have it: the original's wreck is
// its burnt hull texture as it is.
instance uniform vec2 wreck = vec2(0.0);
// the car's steering (Vehicle.Steer, the cockpit's own: +1 / -1 the locks), for the driver's wheel and hand (CarNode)
instance uniform float steer = 0.0;
instance uniform float lean = 0.0;         // CarNode: the driver thrown against the car's sideways acceleration (a spring; + to the right)
instance uniform float idle_phase = 0.0;   // CarNode: each driver's own time offset for his idle motion
varying vec3 v_local;
varying vec3 v_nlocal;
// the car's cabin box (MeshBuilder.CabinFit: CUSTOM0 its low corner, CUSTOM1 its high, in the mesh's frame) and the eye in that frame
varying flat vec4 v_cab_a;
varying flat vec4 v_cab_b;
varying flat vec4 v_eye;   // the F1 view's eye in the mesh's frame (w 1), CUSTOM2: where the driver sits
varying vec3 v_cam_l;
global uniform bool i76_car_materials;   // Ctrl+F8 (a debug key): off, every car without its glass (to compare)
global uniform bool i76_car_interiors;   // Graphic Detail's Car Interiors: the proxy cabin and driver behind the glass (off: the glass alone)
uniform bool has_glass = false;    // with car_paint: glass_tex marks the window texels (CarMaterialTable's glass, MaterialLibrary.GlassOf)
uniform sampler2D glass_tex : source_color, hint_default_black, filter_linear_mipmap;   // rgb the glass's own colour (blurred), a the soft mask
uniform sampler2D glass_body : source_color, hint_default_black, filter_linear_mipmap;   // the body's colour round the window, past its trim (MaterialLibrary.BodyColour)
uniform sampler2D glass_frame : source_color, hint_default_transparent, filter_nearest_mipmap;   // a: the texel was painted as glass; rgb then the frame's colour beside it (MaterialLibrary.FrameFill)
// a respray's weights round the glass (MaterialLibrary.GlassPaint): rg the body's (glass_body), ba the frame fill's (glass_frame); and the trim line's
uniform sampler2D glass_paint : hint_default_black, filter_linear_mipmap;
uniform sampler2D glass_trim_paint : hint_default_black, filter_linear_mipmap;

// value noise and its sums, for the char's patches and the cracks
float w_hash(vec3 p) {
	p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float w_noise(vec3 x) {
	vec3 i = floor(x), f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(w_hash(i), w_hash(i + vec3(1, 0, 0)), f.x), mix(w_hash(i + vec3(0, 1, 0)), w_hash(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(w_hash(i + vec3(0, 0, 1)), w_hash(i + vec3(1, 0, 1)), f.x), mix(w_hash(i + vec3(0, 1, 1)), w_hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}
float w_fbm(vec3 p) { return 0.55 * w_noise(p) + 0.3 * w_noise(p * 2.03 + 17.1) + 0.15 * w_noise(p * 4.11 + 3.7); }

// The burnt car's surface: the texture's own detail kept, darkened and mostly greyed, under soot patches,
// heat-browned scorch, the odd rust bloom and bare metal where the paint burnt off; matte, no clear coat.
// While hot the fire inside shows through cracks (ridged noise crawling upward, flickering and pulsing),
// strongest low on the body and in the texture's dark places (the openings, seams, wheel wells, grille):
// white-yellow at full heat, orange, then dull red as it cools.
void burnt(vec3 tex, vec3 seam, vec3 nl, vec2 w, vec3 p, float t, inout vec3 albedo, inout float rough, inout float metal, inout float spec, out vec3 glow) {
	float lum = dot(tex, vec3(0.299, 0.587, 0.114));
	vec3 q = p * 2.4;
	float soot = smoothstep(0.38, 0.72, w_fbm(q));
	float scorch = w_fbm(q * 0.7 + 5.0);
	float flake = w_fbm(q * 4.5 + 11.0);
	vec3 charred = mix(vec3(lum), tex, 0.25) * 0.45 + 0.01;
	// heat-browned and blued where the paint cooked, soot-black where it smoked
	charred *= mix(vec3(1.0), vec3(1.3, 0.85, 0.55), smoothstep(0.45, 0.7, scorch));
	charred = mix(charred, charred * 0.25 + 0.005, soot);
	float rust = smoothstep(0.6, 0.72, w_fbm(q * 1.7 + 7.0)) * (1.0 - soot) * 0.6;
	charred = mix(charred, vec3(0.16, 0.065, 0.025) * (0.6 + lum), rust);
	// bare metal in flakes where the paint burnt off: small, few, dull
	float bare = smoothstep(0.68, 0.74, flake) * (1.0 - soot) * (1.0 - rust);
	charred = mix(charred, vec3(0.16, 0.155, 0.15) * (0.7 + 0.6 * lum), bare);
	albedo = mix(albedo, charred, w.x);
	rough = mix(rough, mix(0.93, 0.5, bare), w.x);
	metal = mix(metal, bare * 0.7, w.x);
	spec = mix(spec, 0.25, w.x);
	glow = vec3(0.0);
	float heat = w.y;
	if (heat <= 0.001) return;
	// where the fire really is: (a) a band along the bottom of the body and round the wheel wells (low in the
	// model, more so where the surface faces down), licking up in tongues; (b) the openings (the texture's
	// darkest regions: windows, grille), glowing from within; (c) thin cracks only on the texture's strong
	// linear edges. Flat paint gets no glow, only a heat tint. Noise only modulates intensity over time.
	float n = w_fbm(p * vec3(3.0, 2.0, 3.0) + vec3(0.0, -t * 0.5, t * 0.13));
	float flick = 0.55 + 0.45 * w_noise(p * 7.0 + vec3(0.0, -t * 2.4, t * 1.3));
	float pulse = 0.8 + 0.2 * sin(t * 2.3 + w_noise(p * 0.9) * 6.283);
	float down = smoothstep(0.1, -0.7, nl.y);
	float lick = 0.22 * n + 0.18 * w_noise(p * 5.0 + vec3(0.0, -t * 1.6, 0.0));
	float band = 1.0 - smoothstep(-0.3, 0.2 + lick, p.y + 0.3 * down);
	float hole = seam.y * (0.55 + 0.45 * flick) * (0.5 + 0.5 * (0.5 + 0.5 * band));
	float crack = seam.x * (0.35 + 0.65 * smoothstep(0.35, 0.6, n)) * (0.4 + 0.6 * band);
	float vein = clamp(max(hole, crack), 0.0, 1.0);
	float amount = (band * 0.9 * (0.45 + 0.55 * flick) * (0.2 + 0.8 * smoothstep(0.3, 0.6, n)) + hole * 1.2 + crack * 0.8 + seam.z * band * 0.2) * pulse;
	// heat tint on the paint near the fire: dark straw, then blue-purple tempering further off
	vec3 temper = mix(vec3(0.5, 0.42, 0.2), vec3(0.2, 0.2, 0.42), smoothstep(0.2, 0.7, p.y + 0.3 * n));
	albedo = mix(albedo, albedo * temper * 3.0 + 0.01, w.x * heat * (1.0 - smoothstep(0.1, 0.9, p.y)) * 0.5);
	// colour by temperature, blackbody: deep red at the edges, orange core, yellow only in thin hottest streaks;
	// the band is broken into flame-shaped licks (stretched noise scrolling upward) rather than a smooth gradient
	float lickn = w_noise(vec3(p.x * 9.0 + p.z * 9.0, p.y * 2.5 - t * 2.6, p.z * 3.0 + p.x * 3.0));
	float tongues = smoothstep(0.35, 0.75, lickn + 0.35 * (band - 0.5));
	amount *= mix(0.25, 1.0, tongues);
	float core = clamp(amount * 1.3, 0.0, 1.0);
	vec3 hot = mix(vec3(0.7, 0.05, 0.01), vec3(1.0, 0.33, 0.04), smoothstep(0.1, 0.5, core));
	hot = mix(hot, vec3(1.0, 0.62, 0.15), smoothstep(0.7, 1.0, core) * smoothstep(0.5, 0.9, heat));
	hot = mix(hot, vec3(1.0, 0.8, 0.4), smoothstep(0.75, 1.0, heat) * vein * tongues);
	hot = mix(hot, hot * vec3(1.0, 0.45, 0.2), smoothstep(0.6, 0.2, heat));
	glow = hot * amount * (heat * heat * 1.9 + heat * 0.8);
	albedo = mix(albedo, albedo * 0.4, clamp(amount * heat, 0.0, 1.0));
}
#endif

#ifdef FAITHFUL
// The cars' night lights on a vertex at w (global) with the outward normal n, of an object whose origin is o:
// the four that reach o strongest, each cone · 1/(1 + d²/range²) · (−nose·n) · 1.25 (NightSpot in I76.Formats). The cone is ((1 + cos)/2)^exponent, cos from the nose to the point over |d| + 0.5.
float night_cone(vec3 to_point, vec3 fwd, float e) {
	float s = (1.0 + dot(to_point, fwd) / (length(to_point) + 0.5)) * 0.5;
	return s > 0.001 ? pow(s, e) : 0.0;
}

float night_lights(vec3 w, vec3 n, vec3 o) {
	int pick[4] = int[](-1, -1, -1, -1);
	float best[4] = float[](-1.0, -1.0, -1.0, -1.0);
	for (int i = 0; i < i76_night_light_count; i++) {
		vec4 a = texelFetch(i76_night_lights, ivec2(i, 0), 0);
		vec4 b = texelFetch(i76_night_lights, ivec2(i, 1), 0);
		vec3 to_o = o - a.xyz;
		float str = night_cone(to_o, b.xyz, a.w) / (b.w * dot(to_o, to_o) + 1.0);
		int lo = 0;
		for (int k = 1; k < 4; k++) if (best[k] < best[lo]) lo = k;
		if (str > best[lo]) { best[lo] = str; pick[lo] = i; }
	}
	float sum = 0.0;
	for (int k = 0; k < 4; k++) {
		if (pick[k] < 0) continue;
		vec4 a = texelFetch(i76_night_lights, ivec2(pick[k], 0), 0);
		vec4 b = texelFetch(i76_night_lights, ivec2(pick[k], 1), 0);
		float facing = -dot(b.xyz, n);
		if (facing <= 0.0) continue;
		vec3 to_w = w - a.xyz;
		sum += night_cone(to_w, b.xyz, a.w) / (b.w * dot(to_w, to_w) + 1.0) * facing * 1.25;
	}
	return sum;
}
#endif


// The colour a paint of colour t (sRGB, alpha its finish code) gives a texel s (sRGB) of luma y whose paint has mean luma yref: the target scaled by
// the texel's shade relative to the paint's mean (the painting's own highlights and shadows kept), the overflow past 1 rolled off to white, as
// ResprayPaint.Recolour does. Metallic reads steeper (shade^1.35). Blended into cur by the mask weight w.
vec3 paint_apply(vec3 cur, float y, float w, vec4 t, float yref) {
	float shade = pow((y + 0.05) / (yref + 0.05), t.a > 1.5 && t.a < 2.5 ? 1.35 : 1.0);
	vec3 col = t.rgb * shade;
	float k = min(max(0.0, max(col.r, max(col.g, col.b)) - 1.0) * 0.5, 1.0);
	col = min(col, vec3(1.0)) * (1.0 - k) + vec3(k);
	return mix(cur, col, w);
}

// A colour (linear) baked from the scheme's own paint, resprayed by its weights (primary, secondary) as the body texels are.
vec3 paint_respray(vec3 lin, vec2 w) {
	if (!has_paint_mask || (paint_a.a < 0.5 && paint_b.a < 0.5) || w.x + w.y < 0.004) return lin;
	vec3 srgb = to_srgb(lin);
	float luma = dot(srgb, vec3(0.299, 0.587, 0.114));
	srgb = paint_apply(srgb, luma, paint_a.a > 0.5 ? w.x : 0.0, paint_a, paint_luma.x);
	srgb = paint_apply(srgb, luma, paint_b.a > 0.5 ? w.y : 0.0, paint_b, paint_luma.y);
	return to_linear(srgb);
}

void vertex() {
#ifndef FAITHFUL
	v_local = VERTEX;
	v_nlocal = NORMAL;
	v_cab_a = CUSTOM0;
	v_cab_b = CUSTOM1;
	v_eye = CUSTOM2;
	// the camera in the mesh's frame: the transpose of a scaled rotation is the inverse times the scale squared (the 3D garage fits a big
	// vehicle to the bay by scaling it), so divided by that
	v_cam_l = has_glass ? transpose(mat3(MODEL_MATRIX)) * (CAMERA_POSITION_WORLD - MODEL_MATRIX[3].xyz) / max(dot(MODEL_MATRIX[0].xyz, MODEL_MATRIX[0].xyz), 1e-6) : vec3(0.0);
#endif
#ifdef FAITHFUL
	vec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 n_sun = dot(card_y, card_y) > 0.0 ? normalize(mat3(card_x, card_y, card_z) * NORMAL) : n;
	float d = max(0.0, dot(n_sun, normalize(i76_light_dir)));
	float depth = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
	float fade = i76_night ? 1.0 : clamp(depth * i76_fade.x + i76_fade.y, 0.75, 1.0);
	float lit = fade * d * i76_light_intensity;
	if (i76_night && i76_night_light_count > 0)
		lit += night_lights((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz, n, MODEL_MATRIX[3].xyz);
	v_intensity = i76_ramp(lit + i76_ambient);
#endif
}

#ifndef FAITHFUL

// ---- windows: dark tinted glass over a vague proxy cabin -------------------------------------------------------------------------
// No cabin is modelled. A window texel (glass_tex) shows what a ray into the cabin box would see, as a blur: the cabin's walls (dark below
// the sill, a lighter "outdoors" through the other windows above it) with a few soft dark masses where the driver's head and shoulders and
// the seat backs are, as through tinted, slightly dirty glass. The result is an albedo, not an emission: the scene's own light (sun, sky
// ambient, the garage's lamps, the night) lights it as it lights the paint, so it sits in every scene. Left-hand drive.

// Primitives and blends after Inigo Quilez (iquilezles.org/articles/distfunctions): ellipsoid, capsule, round cone, round box, torus, the
// polynomial smooth minimum, the tetrahedral normal and the five-tap occlusion.
float gl_dot2(vec3 v) { return dot(v, v); }
float gl_ellipsoid(vec3 p, vec3 r) { float k0 = length(p / r), k1 = max(length(p / (r * r)), 1e-6); return k0 * (k0 - 1.0) / k1; }   // k1 is 0 at the centre
float gl_capsule(vec3 p, vec3 a, vec3 b, float r) {
	vec3 pa = p - a, ba = b - a;
	return length(pa - ba * clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0)) - r;
}
float gl_roundcone(vec3 p, vec3 a, vec3 b, float r1, float r2) {
	vec3 ba = b - a;
	float l2 = dot(ba, ba), rr = r1 - r2, a2 = l2 - rr * rr, il2 = 1.0 / l2;
	vec3 pa = p - a;
	float y = dot(pa, ba), z = y - l2;
	float x2 = gl_dot2(pa * l2 - ba * y), y2 = y * y * l2, z2 = z * z * l2;
	float k = sign(rr) * rr * rr * x2;
	if (sign(z) * a2 * z2 > k) return sqrt(x2 + z2) * il2 - r2;
	if (sign(y) * a2 * y2 < k) return sqrt(x2 + y2) * il2 - r1;
	return (sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
}
float gl_roundbox(vec3 p, vec3 c, vec3 h, float r) {
	vec3 q = abs(p - c) - h + r;
	return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0) - r;
}
float gl_smin(float a, float b, float k) { float h = max(k - abs(a - b), 0.0) / k; return min(a, b) - h * h * k * 0.25; }

// the cabin's shapes round the driver's head (origin; x right, y up, z toward the rear, the car faces -z): the figure (one smooth
// union), his seat, the other front seat, the dash; id: 1 skin, 2 shirt, 3 trousers, 4 seat, 5 wheel, 6 dash. seat.x is the other seat's
// offset, seat.y the bench's z (0: none); simple: the far figure, without arms, hands, wheel and legs
float gl_map(vec3 p, vec4 seat, float who, bool simple, mat3 pose, out float id) {
	vec3 q = vec3(abs(p.x), p.y, p.z);   // he is symmetric
	float d = 1e3;
	id = 4.0;
	// the figure's parts cost a dozen primitives: away from his bounding box only its distance is used
	float bound = gl_roundbox(p, vec3(0.0, -0.4, -0.05), vec3(0.27, 0.62, 0.5), 0.05);
	if (who > 0.5 && bound > 0.1) { d = bound; id = 2.0; }
	else if (who > 0.5) {
		// his upper body sways about his hips: pose[2].y leans it sideways (x, the most at his head), pose[2].z lifts it (breath), the hips
		// and legs stay put and his left hand stays on the wheel
		float up = clamp((p.y + 0.7) / 0.7, 0.0, 1.3);
		vec3 pb = p - vec3(pose[2].y * up, pose[2].z * up, 0.0);
		vec3 qb = vec3(abs(pb.x), pb.y, pb.z);
		float head = gl_ellipsoid(pb - vec3(0.0, 0.0, -0.005), vec3(0.088, 0.115, 0.1));
		float jaw = gl_ellipsoid(pb - vec3(0.0, -0.075, -0.03), vec3(0.062, 0.07, 0.07));
		float neck = gl_capsule(pb, vec3(0.0, -0.1, 0.025), vec3(0.0, -0.21, 0.05), 0.048);
		float skin = gl_smin(gl_smin(head, jaw, 0.05), neck, 0.05);
		// torso: a cone from the shoulders to the waist, flattened front to back, and the shoulders' bar (his breath swells the chest)
		vec3 tp = vec3(qb.x, qb.y, qb.z * 1.35 - 0.03);
		float torso = gl_roundcone(tp, vec3(0.0, -0.3, 0.1), vec3(0.0, -0.7, 0.1), 0.18 + pose[2].z * 0.8, 0.13) * 0.8;
		float sh = gl_capsule(qb, vec3(0.0, -0.26, 0.06), vec3(0.17, -0.26, 0.06), 0.07);
		float shirt = gl_smin(torso, sh, 0.06);
		float thigh = 1e3;
		if (!simple) {
			// his left hand rides the wheel's rim where the cockpit's steering sprite puts it (pose[0] the hand, pose[1] the elbow, pose[2].x the
			// wheel's turn), his right arm rests, the hand on his thigh: the sprite shows the left hand only
			vec3 bs = vec3(pose[2].y * 0.63, pose[2].z * 0.63, 0.0);   // the shoulders ride with the body
			vec3 sl = vec3(-0.17, -0.26, 0.06) + bs, sr = vec3(0.17, -0.26, 0.06) + bs;
			vec3 er = vec3(0.24, -0.5, 0.0) + bs * 0.5, hr = vec3(0.15, -0.64, -0.2);
			shirt = gl_smin(shirt, gl_roundcone(p, sl, pose[1], 0.058, 0.046), 0.05);
			shirt = gl_smin(shirt, gl_roundcone(p, pose[1], pose[0], 0.046, 0.034), 0.04);
			shirt = gl_smin(shirt, gl_roundcone(p, sr, er, 0.058, 0.046), 0.05);
			shirt = gl_smin(shirt, gl_roundcone(p, er, hr, 0.046, 0.034), 0.04);
			skin = min(skin, gl_ellipsoid(p - pose[0] - vec3(0.0, 0.0, -0.015), vec3(0.04, 0.034, 0.05)));
			skin = min(skin, gl_ellipsoid(p - hr, vec3(0.04, 0.03, 0.05)));
			thigh = gl_roundcone(q, vec3(0.1, -0.73, 0.1), vec3(0.11, -0.7, -0.38), 0.085, 0.065);
			// the wheel, tilted back toward him
			vec3 n = normalize(vec3(0.0, 0.45, 0.89)), ey = normalize(cross(n, vec3(1.0, 0.0, 0.0)));
			vec3 wp = p - vec3(0.0, -0.34, -0.41);
			float cp = cos(pose[2].x), sp2 = sin(pose[2].x);
			vec2 wxy = vec2(wp.x, dot(wp, ey));
			vec2 rw = vec2(cp * wxy.x + sp2 * wxy.y, -sp2 * wxy.x + cp * wxy.y);   // the wheel's own frame, turned by its steering
			vec2 tq = vec2(length(wxy) - 0.19, dot(wp, n));
			float wheel = length(tq) - 0.017;
			// the hub and three spokes (one down, two across) turn with it
			float spokes = min(gl_capsule(vec3(rw, dot(wp, n)), vec3(-0.19, 0.0, 0.0), vec3(0.19, 0.0, 0.0), 0.012), gl_capsule(vec3(rw, dot(wp, n)), vec3(0.0, 0.0, 0.0), vec3(0.0, -0.19, 0.0), 0.012));
			wheel = min(wheel, spokes);
			if (wheel < d) { d = wheel; id = 5.0; }
		}
		if (skin < d) { d = skin; id = 1.0; }
		if (shirt < d) { d = shirt; id = 2.0; }
		if (thigh < d) { d = thigh; id = 3.0; }
	}
	float seatd = gl_roundbox(p, vec3(0.0, -0.46, 0.23), vec3(0.22, 0.3, 0.07), 0.05);
	seatd = min(seatd, gl_roundbox(p, vec3(0.0, -0.05, 0.21), vec3(0.09, 0.07, 0.035), 0.03));
	seatd = min(seatd, gl_roundbox(p, vec3(0.0, -0.8, -0.05), vec3(0.22, 0.05, 0.24), 0.03));
	vec3 po = p - vec3(seat.x, 0.0, 0.0);
	seatd = min(seatd, gl_roundbox(po, vec3(0.0, -0.46, 0.23), vec3(0.22, 0.3, 0.07), 0.05));
	seatd = min(seatd, gl_roundbox(po, vec3(0.0, -0.05, 0.21), vec3(0.09, 0.07, 0.035), 0.03));
	seatd = min(seatd, gl_roundbox(po, vec3(0.0, -0.8, -0.05), vec3(0.22, 0.05, 0.24), 0.03));
	if (seat.y > 0.5 && seat.z < -0.5) {
		// the limo (seat.z minus the compartment's length behind the driver): a rear-facing bench against the partition, a forward-facing
		// one across the back, and a bench along each side between them
		float zf = seat.y, zb = -seat.z - 0.2, cx = seat.x * 0.5;
		seatd = min(seatd, gl_roundbox(p, vec3(cx, -0.42, zf), vec3(0.7, 0.32, 0.06), 0.05));
		seatd = min(seatd, gl_roundbox(p, vec3(cx, -0.8, zf + 0.28), vec3(0.7, 0.05, 0.24), 0.03));
		seatd = min(seatd, gl_roundbox(p, vec3(cx, -0.42, zb), vec3(0.7, 0.32, 0.06), 0.05));
		seatd = min(seatd, gl_roundbox(p, vec3(cx, -0.8, zb - 0.28), vec3(0.7, 0.05, 0.24), 0.03));
		float zm = 0.5 * (zf + zb), hl = max(0.5 * (zb - zf) - 0.6, 0.1);
		vec3 ps = vec3(abs(p.x - cx), p.y, p.z);
		seatd = min(seatd, gl_roundbox(ps, vec3(0.52, -0.8, zm), vec3(0.2, 0.05, hl), 0.03));
		seatd = min(seatd, gl_roundbox(ps, vec3(0.72, -0.45, zm), vec3(0.05, 0.3, hl), 0.04));
	} else if (seat.y > 0.5 && seat.z > 2.5) {
		// the bus: a row a window (0.75 m) from behind the driver to the back, seat.z - 3 of them, each two tall-backed benches either
		// side of the aisle (a limited repetition: the nearest row's distance)
		float n = seat.z - 3.0;
		vec3 pr = p - vec3(0.0, seat.w, 0.0);
		pr.z -= 0.75 * clamp(round((p.z - seat.y) / 0.75), 0.0, n - 1.0);
		vec3 lq = vec3(abs(pr.x - seat.x * 0.5) - 0.72, pr.y, pr.z);
		seatd = min(seatd, gl_roundbox(lq, vec3(0.0, -0.36, seat.y), vec3(0.45, 0.36, 0.06), 0.05));
		seatd = min(seatd, gl_roundbox(lq, vec3(0.0, -0.8, seat.y - 0.24), vec3(0.45, 0.05, 0.2), 0.03));
	} else if (seat.y > 0.5) {
		// the rear row: a bench across the cabin with two headrests
		vec3 pr = p;
		float hw = max(abs(seat.x) * 0.5 + 0.3, 0.5);
		seatd = min(seatd, gl_roundbox(pr, vec3(seat.x * 0.5, -0.4, seat.y), vec3(hw, 0.3, 0.08), 0.05));
		seatd = min(seatd, gl_roundbox(pr, vec3(seat.x * 0.5 - hw * 0.55, -0.03, seat.y - 0.02), vec3(0.09, 0.09, 0.04), 0.03));
		seatd = min(seatd, gl_roundbox(pr, vec3(seat.x * 0.5 + hw * 0.55, -0.03, seat.y - 0.02), vec3(0.09, 0.09, 0.04), 0.03));
		seatd = min(seatd, gl_roundbox(pr, vec3(seat.x * 0.5, -0.8, seat.y - 0.3), vec3(hw, 0.05, 0.25), 0.03));
	}
	if (seatd < d) { d = seatd; id = 4.0; }
	float dash = gl_roundbox(p, vec3(seat.x * 0.5, -0.66, -0.68), vec3(abs(seat.x) * 0.5 + 0.6, 0.14, 0.2), 0.05);
	if (dash < d) { d = dash; id = 6.0; }
	return d;
}

// The cabin seen along a car-frame ray from a window point: an albedo (1 is white paint), before the glass's tint.
vec3 gl_cabin(vec3 ro, vec3 rd, float px, float detail, float dist, float who, vec3 cab_lo, vec3 cab_hi, float rows, vec4 eye, float steer_in) {
	vec3 lo = vec3(cab_lo.x + 0.05, cab_lo.y - 0.5, cab_lo.z + 0.02), hi = vec3(cab_hi.x - 0.05, cab_hi.y + 0.01, cab_hi.z - 0.02);
	vec3 inv = 1.0 / (sign(rd + vec3(1e-9)) * max(abs(rd), vec3(1e-4)));
	vec3 t1 = (lo - ro) * inv, t2 = (hi - ro) * inv;
	vec3 tn = min(t1, t2), tf = max(t1, t2);
	float t0 = max(max(max(tn.x, tn.y), tn.z), 0.0), t_exit = min(min(tf.x, tf.y), tf.z);
	if (t_exit <= t0) return vec3(0.012);
	vec3 pe = ro + rd * t_exit;
	// the wall the ray leaves by: the floor and roof are dark, the doors below the sill are dark, above it the other windows show the outdoors
	float sill = cab_lo.y - 0.02;
	float open_wall = (tf.y > t_exit + 1e-5) ? smoothstep(sill - 0.06, sill + 0.14, pe.y) * (1.0 - 0.8 * smoothstep(cab_hi.y - 0.1, cab_hi.y, pe.y)) : 0.0;
	float out_tone = mix(0.12, 0.22, smoothstep(-0.4, 0.2, rd.y));
	float wall = mix(0.06 * (0.5 + 0.5 * smoothstep(lo.y, hi.y, pe.y)), out_tone, open_wall);
	vec3 col = vec3(wall);
	if (detail <= 0.0) return col;
	float xc = 0.5 * (lo.x + hi.x);
	float zd = rows > 1.5 ? lo.z + clamp(0.36 * (hi.z - lo.z), 0.8, 1.0) : lo.z + clamp(0.5 * (hi.z - lo.z), 0.85, 1.3);
	float xd = xc - clamp(0.45 * (hi.x - lo.x) * 0.5, 0.24, 0.40);
	float hy = min(hi.y - 0.13, cab_lo.y + 0.5);
	vec3 head = vec3(xd, hy, zd);
	// the driver's eyes are the F1 view's: his head's centre a few centimetres behind and above them (the cabin's box only if the car has none)
	if (eye.w > 0.5) { head = eye.xyz + vec3(0.0, -0.005, 0.09); xd = head.x; zd = head.z; hy = head.y; }
	// and his head clear of the roof (the cabin box's top is the glass's; the headliner sits a little lower)
	head.y = min(head.y, cab_hi.y - 0.16); hy = head.y;
	// a rear bench: four-door cars always, a two-door one when the cabin runs a metre behind the driver (a 2+2: the Palomino's back seat
	// through its rear window)
	float room = hi.z - 0.1 - zd;
	float rear = rows > 1.5 ? max(0.6, min(0.9, room)) : (room > 1.0 ? min(0.9, room) : 0.0);
	// seat.w: how far the passenger rows sit above the driver's (a coach's raised floor: their heads at the side windows' middle,
	// a school bus's no higher than his)
	// (rows 5: the coach, its passengers half a metre above the driver on the raised floor)
	vec4 seat = vec4(2.0 * (xc - xd), rear, rows, rows > 4.5 ? 0.5 : rows > 2.5 && rows < 3.5 ? max(0.0, (cab_lo.y + cab_hi.y) * 0.5 - hy) : 0.0);
	// the bus: its rows to the cabin's back (seat.z 3 + their count)
	if (rows > 3.5 && rows < 4.5) seat.z = -max(hi.z - zd, rear + 1.5);
	else if (rows > 2.5) seat.z = 3.0 + max(1.0, floor((hi.z - zd - rear - 0.3) / 0.75) + 1.0);
	// the figure's bounding box (head frame)
	vec3 o = ro - head;
	// the steering sprite's mapping: frame 9 straight, 1 at +1 (hand up the rim to the 10 o'clock side), 36 at -1 (down and
	// across to the 3 o'clock side); read off the 36 frames as the hand's angle round the rim, 232 deg at rest, 9.6 deg a frame one way, 5.9 the other
	float fr = clamp(steer_in < 0.0 ? 9.0 + 27.0 * -steer_in : 9.0 - 9.0 * steer_in, 1.0, 36.0);
	float phi = radians(fr < 9.0 ? 232.0 - (9.0 - fr) * 9.6 : 232.0 + (fr - 9.0) * 5.9);
	vec3 wn = normalize(vec3(0.0, 0.45, 0.89)), wey = normalize(cross(wn, vec3(1.0, 0.0, 0.0)));
	vec3 whand = vec3(0.0, -0.34, -0.41) + 0.19 * (cos(phi) * vec3(1.0, 0.0, 0.0) + sin(phi) * wey);
	vec3 shl = vec3(-0.17, -0.26, 0.06);
	// the elbow: two limbs of 0.275 between shoulder and hand, bent down and out
	vec3 sh_h = whand - shl;
	float dl = max(length(sh_h), 1e-4);
	vec3 axis = sh_h / dl, pole = vec3(-0.5, -1.0, 0.2);
	pole = normalize(pole - axis * dot(pole, axis));
	vec3 elbow = shl + 0.5 * sh_h + pole * sqrt(max(0.275 * 0.275 - 0.25 * dl * dl, 0.0));
	// his body: the lean (CarNode's spring, about 12 cm at his head a unit) and an idle motion, a slow breath (half a centimetre) and a
	// small drift side to side, each driver on his own time
	float it = TIME + idle_phase;
	float bx = clamp(lean, -1.5, 1.5) * 0.12 + 0.008 * sin(it * 0.43) + 0.004 * sin(it * 1.07 + 1.3);
	float by = 0.005 * sin(it * 1.5);
	mat3 pose = mat3(whand, elbow, vec3(phi - radians(232.0), bx, by));
	vec3 bl = vec3(-0.4, -1.0, -0.9), bh = vec3(max(seat.x, 0.0) + 0.4, 0.2, rear > 0.5 ? max(rear + 0.3, hi.z - zd) : 0.45);
	if (seat.x < 0.0) { bl.x = seat.x - 0.4; bh.x = 0.4; }
	bh.y += seat.w;
	if (seat.z > 2.5 || seat.z < -0.5) { bl.x = min(bl.x, seat.x * 0.5 - 1.25); bh.x = max(bh.x, seat.x * 0.5 + 1.25); }
	vec3 b1 = (bl - o) * inv, b2 = (bh - o) * inv;
	float tb0 = max(max(max(min(b1.x, b2.x), min(b1.y, b2.y)), min(b1.z, b2.z)), t0), tb1 = min(min(min(max(b1.x, b2.x), max(b1.y, b2.y)), max(b1.z, b2.z)), t_exit);
	if (tb1 <= tb0) return col;
	bool simple = dist > 13.0;
	float t = tb0, id = 0.0, dmin = 1e3, idmin = 4.0, tmin = tb0;
	bool hit = false;
	int steps = simple ? 16 : (seat.z > 2.5 || seat.z < -0.5 ? 48 : 30);
	for (int i = 0; i < steps; i++) {
		float d = gl_map(o + rd * t, seat, who, simple, pose, id);
		if (d < dmin) { dmin = d; idmin = id; tmin = t; }
		if (d < 0.004) { hit = true; break; }
		t += max(d * 0.9, 0.004);
		if (t > tb1) break;
	}
	// a soft silhouette, not a shaded model: the nearest the ray passed to the figure is a coverage, blurred over several centimetres, more
	// with distance and the pixel's footprint (dirty glass, a distant eye); one dark tone near the cabin's, which the scene lights gently
	float blur = 0.05 + 0.006 * dist + px * 2.0;
	float cov = hit ? 1.0 : 1.0 - smoothstep(0.0, blur, dmin);
	cov *= 0.92;
	vec3 obj = vec3(idmin > 3.5 ? 0.012 : 0.008);
	return mix(col, obj, cov * detail);
}

// The tread's height at (u, v): u counts tread blocks round the tyre (a whole number a revolution, so it wraps), v crosses
// the width. Three lengthwise grooves leave four ribs cut into blocks by slanted cross grooves (each rib's blocks offset from
// its neighbours'), the shoulders rounded off.
float tread_height(vec2 p) {
	float v = p.y;
	float groove = 0.0;
	for (int i = 1; i <= 3; i++) groove = max(groove, 1.0 - smoothstep(0.018, 0.05, abs(v - 0.25 * float(i))));
	float rib = clamp(floor(v * 4.0), 0.0, 3.0);
	float slant = (fract(v * 4.0) - 0.5) * 0.35 * (mod(rib, 2.0) * 2.0 - 1.0);
	float cu = fract(p.x + slant + 0.5 * mod(rib, 2.0));
	float cross_g = 1.0 - smoothstep(0.035, 0.1, min(cu, 1.0 - cu));
	float shoulder = smoothstep(0.0, 0.07, v) * smoothstep(1.0, 0.93, v);
	return clamp(1.0 - max(groove, cross_g * 0.85), 0.0, 1.0) * mix(0.5, 1.0, shoulder);
}
#endif

void fragment() {
	vec2 tuv = UV;
	float disc_d = 0.0;
#ifdef KEY_COVERAGE
	if (round_key) {
		vec2 off = UV - vec2(0.5);
		disc_d = length(off);
		tuv = vec2(0.5) + off * min(1.0, 0.465 / max(disc_d, 0.0001));   // no colour from the keyed texels past the rim
	}
#endif
#ifdef FAITHFUL
	vec4 c = use_texture ? i76_tex(prelit ? i76_cockpit(tuv) : i76_software ? i76_point(albedo_tex, tuv) : texture(albedo_tex, tuv)) : (i76_software && albedo_color_software.a > 0.5 ? albedo_color_software : albedo_color);
#else
	vec4 c = use_texture ? (prelit ? i76_cockpit(tuv) : texture(albedo_tex, tuv)) : albedo_color;
#endif
	// a respray: the scheme's paint texels (the mask) take the car's own colours; pf_metal / pf_matte the finish where they did
	float pf_metal = 0.0;
	float pf_matte = 0.0;
	if (has_paint_mask && (paint_a.a > 0.5 || paint_b.a > 0.5)) {
#ifdef FAITHFUL
		vec2 pm = (i76_software ? i76_point(paint_mask, tuv) : texture(paint_mask, tuv)).rg;
#else
		vec2 pm = texture(paint_mask, tuv).rg;
#endif
		float wa = paint_a.a > 0.5 ? pm.r : 0.0;
		float wb = paint_b.a > 0.5 ? pm.g : 0.0;
		vec3 srgb = to_srgb(c.rgb);
		float luma = dot(srgb, vec3(0.299, 0.587, 0.114));
		srgb = paint_apply(srgb, luma, wa, paint_a, paint_luma.x);
		srgb = paint_apply(srgb, luma, wb, paint_b, paint_luma.y);
		c.rgb = to_linear(srgb);
		float code = wa >= wb ? paint_a.a : paint_b.a;
		float wmax = max(wa, wb);
		pf_metal = code > 1.5 && code < 2.5 ? wmax : 0.0;
		pf_matte = code > 2.5 ? wmax : 0.0;
	}
#ifdef KEY_COVERAGE
	float key_cov = round_key ? clamp((0.495 - disc_d) / max(fwidth(disc_d), 0.0001) + 0.5, 0.0, 1.0)
		: alpha_key ? smoothstep(0.1, 0.9, c.a) : 1.0;
	if (key_cov <= 0.0) discard;
#else
	if (alpha_key && c.a < 0.5) discard;
#endif
#ifdef FAITHFUL
	ALBEDO = i76_out(to_linear(prelit || unlit_1997 ? to_srgb(c.rgb) : i76_shade(to_srgb(c.rgb), v_intensity)));
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
#else
	if (prelit) {
		ALBEDO = vec3(0.0);
		EMISSION = c.rgb;
		ROUGHNESS = 1.0;
		SPECULAR = 0.0;
	} else if (car_paint) {
		// the faces turned to the ground (the chassis under the car, the wheel arches' tops seen from below) are not
		// paint: matte, no clear coat. Mirror-coated, in the shade, they showed nothing but the brown lower half of
		// the sky's radiance, streaked as the horizon is, and read as the ground seen through the car (a car in the air).
		float under = 1.0 - smoothstep(-0.6, -0.15, v_nlocal.y / max(length(v_nlocal), 1e-6));
		ALBEDO = c.rgb;
		ROUGHNESS = mix(0.4, 0.85, under);
		METALLIC = 0.0;
		SPECULAR = mix(0.5, 0.2, under);
		CLEARCOAT = 1.0 - under;
		CLEARCOAT_ROUGHNESS = 0.06;
		// a respray's finish: metallic flake under the coat, or matte paint without one
		METALLIC = pf_metal * 0.75;
		ROUGHNESS = mix(ROUGHNESS, 0.3, pf_metal);
		ROUGHNESS = mix(ROUGHNESS, 0.9, pf_matte);
		SPECULAR = mix(SPECULAR, 0.2, pf_matte);
		CLEARCOAT = mix(CLEARCOAT, 0.0, pf_matte);
		if (normals_debug) { ALBEDO = vec3(0.0); EMISSION = (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz * 0.5 + 0.5; ROUGHNESS = 1.0; SPECULAR = 0.0; CLEARCOAT = 0.0; }
		if (has_glass && i76_car_materials && v_cab_a.w > 0.5) {
			vec4 gt = texture(glass_tex, UV);
			float gm = gt.a;
			float g = clamp((gm - 0.5) / max(fwidth(gm), 1e-3) + 0.5, 0.0, 1.0);   // the windows' polygons (GlassShape) cut at their edge, antialiased over a pixel
			// a texel painted as pane (or its painted edge) that the straightened edge leaves outside the glass shows the frame beside it,
			// not a tooth; and the painted edge's trim is drawn again as a straight line along the glass, the 1.25 texels just outside it (over the painted frame's own steps)
			// (the field: 0.5 at the edge, 0 at GlassShape.FieldReach = 6 texels out)
			vec4 gf = texture(glass_frame, UV);
			vec4 gpw = texture(glass_paint, UV);   // a respray: the surroundings below are baked in the scheme's colours
			ALBEDO = mix(ALBEDO, paint_respray(gf.rgb, gpw.ba), gf.a * (1.0 - g));
			float out_d = (0.5 - gm) * 12.0;   // texels outside the edge (GlassShape.FieldReach = 6)
			float aa = max(fwidth(out_d), 1e-3);
			float px = max(length(fwidth(v_local)), 0.003);   // the pixel's footprint on the car, taken here: derivatives are only defined where every pixel of a 2x2 quad runs (not inside the glass branch)
			// past the trim line, the body's colour up to five texels out, fading back to the painting by five and a half: the painting's
			// stepped edge between trim and body, and the chrome line down a narrow pillar between two panes, are drawn over straight
			float bodyw = clamp((out_d - 1.25) / aa + 0.5, 0.0, 1.0) * (1.0 - smoothstep(4.5, 5.5, out_d)) * (1.0 - g);
			ALBEDO = mix(ALBEDO, paint_respray(texture(glass_body, UV).rgb, gpw.rg), bodyw);
			float band = (1.0 - clamp((out_d - 1.25) / aa + 0.5, 0.0, 1.0)) * (1.0 - g);
			ALBEDO = mix(ALBEDO, paint_respray(gt.rgb, texture(glass_trim_paint, UV).rg), band);
			if (g > 0.0) {
				float dist = length(VERTEX);
				float ndv = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
				float fres = 0.04 + 0.96 * pow(1.0 - ndv, 5.0);
				float detail = 1.0 - smoothstep(14.0, 45.0, dist);
				float drv = 1.0 - clamp(wreck.x * 4.0, 0.0, 1.0);
				vec3 rd = normalize(v_local - v_cam_l);
				// with Car Interiors off the glass is just glass: a dark tinted pane that reflects, nothing behind it
				vec3 tone = i76_car_interiors ? gl_cabin(v_local, rd, px, detail, dist, drv, v_cab_a.xyz, v_cab_b.xyz, v_cab_b.w, v_eye, steer) : vec3(0.035);
				// the cabin's tone is an albedo, lit by the scene as the paint is (sun, sky ambient, lamps, night); the glass tints it with the
				// hue the artist painted it, and a little of that colour itself lies on the pane (a near-black pane's hue is noise: neutral)
				float gl = dot(gt.rgb, vec3(0.2126, 0.7152, 0.0722));
				vec3 tint = mix(vec3(1.0), min(gt.rgb / max(gl, 0.01), vec3(2.5)), smoothstep(0.01, 0.06, gl)) * 0.9;
				ALBEDO = mix(ALBEDO, (vec3(0.004, 0.007, 0.008) + gt.rgb * 0.3 + tone * tint) * (1.0 - fres), g);
				ROUGHNESS = mix(ROUGHNESS, 0.04, g);
				METALLIC = mix(METALLIC, 0.0, g);
				SPECULAR = mix(SPECULAR, 0.3, g);
				CLEARCOAT = mix(CLEARCOAT, 0.0, g);
				CLEARCOAT_ROUGHNESS = 0.03;
			}
		}
		if (wheel_face) {
			// the tyre's rubber (the dark, grey texels) is matte; the hubcap's bright metal keeps a satin sheen
			float metal = smoothstep(0.07, 0.22, dot(c.rgb, vec3(0.299, 0.587, 0.114)));
			ROUGHNESS = mix(0.95, 0.38, metal);
			SPECULAR = mix(0.2, 0.5, metal);
			CLEARCOAT = metal * 0.5;
		}
	} else if (tread) {
		ALBEDO = c.rgb;
		METALLIC = 0.0;
		SPECULAR = 0.25;
		CLEARCOAT = 0.0;
		ROUGHNESS = 0.92;
		if (UV.y < 1.5) {
			// the pattern fades out as a block shrinks toward a pixel (derivatives), to the plain rubber's average
			float px = max(max(fwidth(UV.x), fwidth(UV.y) * 0.5), 1e-5);
			float detail = 1.0 - smoothstep(0.12, 0.4, px);
			float e = 0.01;
			float h = tread_height(UV);
			float hu = (tread_height(UV + vec2(e, 0.0)) - h) / e;
			float hv = (tread_height(UV + vec2(0.0, e)) - h) / e;
			float hh = mix(0.78, h, detail);
			// a faint wear on the rubber so the blocks are not all one value
			float wear = fract(sin(dot(floor(vec2(UV.x, UV.y * 4.0)), vec2(12.9898, 78.233))) * 43758.5453);
			ALBEDO *= (0.35 + 0.65 * hh) * (0.92 + 0.16 * wear * detail + 0.08 * (1.0 - detail));
			ROUGHNESS = mix(0.92, 1.0, 1.0 - hh);
			// the normal, by the cotangent frame as with the material maps
			vec3 dp1 = dFdx(VERTEX), dp2 = dFdy(VERTEX);
			vec2 duv1 = dFdx(UV), duv2 = dFdy(UV);
			vec3 n = normalize(NORMAL);
			vec3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
			vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
			vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;
			float inv = inversesqrt(max(max(dot(t, t), dot(b, b)), 1e-24));
			vec3 nt = normalize(vec3(-hu * 0.05 * detail, -hv * 0.05 * detail, 1.0));
			NORMAL = normalize(mat3(t * inv, b * inv, n) * nt);
		}
	} else if (has_maps) {
		ALBEDO = c.rgb;
		vec4 m = texture(orm_tex, UV);
		ROUGHNESS = m.r;
		METALLIC = m.g;
		SPECULAR = m.a;
		CLEARCOAT = m.b;
		CLEARCOAT_ROUGHNESS = 0.06;
		if (has_normal) {
			// the meshes carry no tangents: the frame from the screen derivatives of position and UV (the
			// cotangent frame), in which n = N - dh/du·∇u - dh/dv·∇v holds whichever way the UVs are mirrored
			vec3 nt = texture(normal_tex, UV).rgb * 2.0 - 1.0;
			vec3 dp1 = dFdx(VERTEX), dp2 = dFdy(VERTEX);
			vec2 duv1 = dFdx(UV), duv2 = dFdy(UV);
			vec3 n = normalize(NORMAL);
			vec3 dp2perp = cross(dp2, n), dp1perp = cross(n, dp1);
			vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
			vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;
			float inv = inversesqrt(max(max(dot(t, t), dot(b, b)), 1e-24));
			NORMAL = normalize(mat3(t * inv, b * inv, n) * nt);
		}
	} else {
		ALBEDO = c.rgb;
		ROUGHNESS = base_roughness;
		METALLIC = base_metallic;
		SPECULAR = base_specular;
		CLEARCOAT = 0.0;
	}
	if (!prelit && wreck.x > 0.0) {
		vec3 a = ALBEDO, g;
		float r = ROUGHNESS, m = METALLIC, s = SPECULAR;
		// the hull's structure read from its texture: x seams (luminance edges from offset samples, and
		// texels darker than their surroundings), y openings (the very dark: windows, grille, wells), z a faint
		// rim along hard edges (a fresnel term)
		vec3 seam = vec3(0.0);
		if (use_texture) {
			vec2 px = 1.0 / vec2(textureSize(albedo_tex, 0));
			vec3 lw = vec3(0.299, 0.587, 0.114);
			float l0 = dot(c.rgb, lw);
			float lx = dot(textureLod(albedo_tex, UV + vec2(px.x, 0.0) * 1.5, 0.0).rgb, lw) - dot(textureLod(albedo_tex, UV - vec2(px.x, 0.0) * 1.5, 0.0).rgb, lw);
			float ly = dot(textureLod(albedo_tex, UV + vec2(0.0, px.y) * 1.5, 0.0).rgb, lw) - dot(textureLod(albedo_tex, UV - vec2(0.0, px.y) * 1.5, 0.0).rgb, lw);
			float edge = smoothstep(0.16, 0.4, length(vec2(lx, ly)));
			float around = dot(textureLod(albedo_tex, UV, 3.0).rgb, lw);
			float dark = smoothstep(0.07, 0.2, around - l0);
			seam.x = clamp(edge * 0.7 + dark * 0.9, 0.0, 1.0);
			seam.y = (1.0 - smoothstep(0.03, 0.12, l0)) * smoothstep(0.08, 0.2, around);
		}
		seam.z = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 5.0);
		burnt(c.rgb, seam, v_nlocal, wreck, v_local, TIME, a, r, m, s, g);
		ALBEDO = a;
		ROUGHNESS = r;
		METALLIC = m;
		SPECULAR = s;
		CLEARCOAT = CLEARCOAT * (1.0 - wreck.x);
		EMISSION = g;
	}
#endif
#ifdef TRANSLUCENT
	ALPHA = opacity;
#endif
#ifdef KEY_COVERAGE
	// the opaque pipeline: ALPHA alone would make these faces transparent (sorted by object, no depth written),
	// a scissor keeps them opaque; the edge (at the scissor, from texel coordinate 0: no mip boost) hands the
	// coverage above to alpha-to-coverage as it is
	ALPHA = key_cov;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	ALPHA_ANTIALIASING_EDGE = 0.0;
	ALPHA_TEXTURE_COORDINATE = vec2(0.0);
#endif
}

#ifdef FAITHFUL
void light() {
	if (prelit) {
		if (LIGHT_IS_DIRECTIONAL) DIFFUSE_LIGHT += vec3(1.0);
	} else if (LIGHT_IS_DIRECTIONAL) {
		DIFFUSE_LIGHT += vec3(1.0);
	}
}
#endif
