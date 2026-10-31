// Modern terrain: lit ground built from PBR materials coloured by the mission's
// own ground, and the roads painted in as a layer (RoadRaster) rather than
// drawn as a second surface.
//
// The ground's colour is the mission's ground texture, not as a picture but
// as noise fitted to it (GroundNoise): fBm octaves with the texture's own
// spread at each scale, plus two broader than it, each one texture read of a
// baked noise tile (value and gradient) at its own scale and turn, turned
// into colour through the texture's colours ranked by brightness (palette),
// so it keeps the histogram and never repeats.
//
// With the fetched materials (tools/fetch-ground.sh, TerrainMaterials:
// ambientCG's CC0 sand, cracked mud, gravel and layered rock) the octaves
// finer than detail_cell are, near the camera, the materials' instead: each
// material's luminance against its own mean, in units of its own spread,
// moves the same palette rank the fine octaves would, so the materials take
// the mission's colours (red rock stays red, snow white) and lend it their
// structure, normals, roughness and occlusion. Which material shows where:
//   rock on steep ground (triplanar, its layers run level on cliff faces, and
//     the palette rank itself banded by height there, strata);
//   gravel in hollows (cavity: how far the ground lies below its
//     surroundings, baked per chunk from the heightfield) and in patches;
//   cracked mud in broad patches on the flats;
//   sand everywhere else;
// each over the ones below by height blending (a layer's high points show
// first, the lower layer fills its cracks). Flat materials are read with two
// lookups at offsets chosen by low-frequency noise (no visible tiling), all
// with textureGrad from the unshifted coordinates (mips stay right); past
// mat_far they are not read at all and the fitted fine octaves take over,
// which settle distant ground to the middle colours as mipmapping would.
//
// Without the materials (a fresh clone) the ground is the fitted noise all the
// way down, bumped by its own gradient and a few value-noise octaves.
//
// A painted ground (PAINTED_GROUND, terrain_lot.gdshader: M15's parking lot,
// GroundMarkings/GroundPaint; natural grounds draw terrain_modern.gdshader,
// without its code) is not fitted noise but asphalt laid out as its texture is, at the texture's own
// repeat (the 1997 look's): the texture's colours with the paint taken out
// (mark_base: asphalt and tyre lanes), the asphalt scan's aggregate over them
// (layer 0 of the materials, the only one then), broad wear, oil stains, and
// the paint drawn from a signed-distance field (crisp at any distance, chipped
// by noise), its mipmapped coverage once a line is narrower than a pixel.
//
// Texture coordinates come from the sample grid (UV = absolute sample
// coordinates), not from world space, so the floating origin can move
// without the ground swimming. Height is the chunk-local VERTEX.y; chunks
// sit at y = 0. Everything below is in the game frame.
// (shader_type is the including shader's.)

uniform sampler2D ground_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D road_layer : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform bool has_road = false;
uniform float repeat_units = 40.0;     // the engine repeats the ground every 8 samples
uniform float chunk_units = 160.0;
uniform float triplanar_sharpness = 4.0;
uniform float bump_texture = 0.3;     // metres of relief per unit of texture brightness
uniform float bump_noise = 2.0;        // scales the noise octaves
uniform float bump_far = 220.0;        // metres where the fine relief has faded out
uniform bool procedural = false;
uniform sampler2D palette : source_color, filter_linear, repeat_disable;
uniform float octave_amp[12];          // each octave's luminance spread, finest first
uniform int octaves = 0;
uniform float finest_cell = 0.15625;   // the finest octave's cell, in metres (one texel)
uniform float palette_slope = 0.2;     // luminance across the palette
// RGBA8 (GroundNoise.NoiseTexture): R the value as 0.5 + v/2, G and B the gradient as 0.5 + g/8. Filtered
// isotropically: the octaves are smooth and fade before a pixel blurs them, and on this GPU an RGBA16F
// texture read anisotropically, a dozen times a pixel in each of two passes, cost 2-3 ms a frame.
uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform float noise_cells = 64.0;      // lattice cells across the baked noise tile
uniform float noise_texels = 8.0;      // its texels per cell

// ---- the materials (TerrainMaterials); layers: 0 sand, 1 cracked mud, 2 gravel, 3 rock ----
uniform bool use_materials = false;
uniform sampler2DArray mat_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;  // colour, height
// The Compatibility renderer (OpenGL) does not sRGB-decode an array texture's source_color on read, as Forward+ does,
// so its albedo comes in raw there and is made linear by hand (a little off through the mip filtering, which is
// then done on the encoded values).
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
vec4 i76_srgb_array(vec4 c) {
	vec3 lo = c.rgb / 12.92, hi = pow((c.rgb + vec3(0.055)) / 1.055, vec3(2.4));
	return vec4(mix(hi, lo, step(c.rgb, vec3(0.04045))), c.a);
}
#else
vec4 i76_srgb_array(vec4 c) { return c; }
#endif
uniform sampler2DArray mat_normal : filter_linear_mipmap_anisotropic, repeat_enable;               // normal xy, roughness, AO
uniform vec3 mat_mean[4];              // each material's mean colour (linear)
uniform vec4 mat_lum_mean;            // their luminance's mean and spread (linear), a layer each
uniform vec4 mat_lum_spread;
uniform vec4 mat_tile = vec4(2.4, 3.2, 1.8, 9.0);      // metres one repeat covers
uniform vec4 mat_tone = vec4(0.15, 0.6, -0.75, -0.3);  // palette rank offset, in spreads
uniform vec4 mat_bump = vec4(0.9, 1.0, 1.1, 1.3);      // normal map strength
uniform float detail_cell = 1.0;       // octaves finer than this are the materials' near the camera
uniform float detail_gain = 1.8;       // the materials' share of the fine spread
uniform vec2 macro_scale = vec2(11.0, 29.0);  // the macro reads' size against the gravel's and the sand's tile
uniform vec2 road_grain = vec2(0.06, 0.14);  // the ground's grain on asphalt and on dirt roads
uniform float asphalt_tone = 0.85;    // asphalt a little darker than its 1997 texture
uniform float road_edge = 1.2;         // road edges broken by the ground's height
uniform float mid_keep = 0.6;          // with the materials, the fitted octaves of 1-20 m kept this much (the macro reads stand in)
uniform float macro_gain = 0.6;        // their luminance's share of the palette spread
uniform float chroma_keep = 0.5;       // how much of the materials' own hue survives the palette
uniform float mat_near = 70.0;         // metres: materials whole to here
uniform float mat_far = 180.0;         // and gone (not read) from here
uniform float rock_near = 220.0;       // the same for rock, which cliffs show from further
uniform float rock_far = 480.0;
uniform float rock_slope_lo = 0.18;    // 1 - normal.y where rock starts showing
uniform float rock_slope_hi = 0.42;    // and where it covers
uniform float strata_amp = 0.9;        // palette spreads of banding with height on rock
uniform float strata_squash = 1.3;     // rock texture squashed vertically on cliffs, layers thinner
uniform float blend_contrast = 6.0;    // height blend: higher, crisper edges between materials
uniform bool has_cavity = false;
uniform sampler2D cavity_tex : filter_linear, repeat_disable;  // per chunk: hollows (R), broad hollows (G), 0.5 = level
uniform float cavity_texels = 35.0;    // the chunk's 33 samples and an apron of one each side
uniform vec2 chunk_origin = vec2(0.0); // the chunk's first sample
uniform int debug_view = 0;            // 1: where the materials are (red cracked mud, green gravel, blue rock), 2: grey; I76_TERRAIN_DEBUG
// How shiny the ground is. Natural ground is nearly matte: the materials' roughness maps (about 0.45-0.95)
// are squeezed into ground_rough, and SPECULAR stays low, which in Godot also caps the Fresnel rise at grazing
// angles (f90 = 50 * F0, F0 = 0.16 * SPECULAR^2): 0.06 (f90 0.03) leaves a faint sheen toward a low sun, where
// 0.25 (f90 0.5) put a pale glaze over sand and rock (M13 at dusk). Asphalt keeps a slight sheen (f90 0.3);
// dirt tracks are as matte as the ground. The 3D horizon's rock (horizon_terrain.gdshader) has none at all.
uniform vec2 ground_rough = vec2(0.9, 1.0);
uniform float ground_specular = 0.06;
uniform vec2 asphalt_shine = vec2(0.8, 0.2);  // roughness, specular
uniform vec2 dirt_road_shine = vec2(0.96, 0.08);

#ifdef PAINTED_GROUND
// ---- a painted ground (GroundPaint) ----
uniform sampler2D mark_base : source_color, filter_linear_mipmap_anisotropic, repeat_enable;  // the texture without its paint
uniform sampler2D mark_field : filter_linear_mipmap_anisotropic, repeat_enable;  // R: signed distance to the paint, G: its coverage
uniform float mark_range = 2.0;       // R spans this many texture texels either side of its edge
uniform float mark_texels = 128.0;    // the texture's texels across one repeat
uniform vec3 paint_colour = vec3(0.6);   // linear
uniform float asphalt_detail = 1.0;   // the scan's colour against the texture's (1: as scanned)
uniform float lot_wear = 0.07;        // broad lighter and darker patches
uniform float oil_stains = 0.5;
uniform float paint_wear = 0.45;      // how chipped the paint is
uniform float lot_relief = 0.12;      // the share of the broad undulation a lot keeps
#endif

#include "res://Shaders/brush_cursor.gdshaderinc"
#ifndef PAINTED_GROUND
// The sward (World/Vegetation.cs, SwardMap; docs/modern-vegetation.md): how grassy the ground between the plants is,
// a byte a 4 m texel over the map's tiles (rect: its game-frame corner and 1 / its size in metres), and the dry
// grass's colour (linear) with the strength in a (0: no vegetation, the 1997 look's terrain never reads it).
global uniform sampler2D i76_sward : filter_linear, repeat_disable;
global uniform vec4 i76_sward_rect;
global uniform vec4 i76_sward_tint;
uniform float sward_cover = 0.3;       // how far a closed sward shifts the ground toward dry grass (the ground stays dominant)
uniform float sward_keep_lum = 0.6;    // how much of the ground's own lightness the shift keeps (the land's identity)

#endif

varying vec3 g_pos;     // game-frame position: sample * 5, height
varying vec3 g_nrm;
varying vec3 g_broad;   // the surface's normal over a broad span (CUSTOM2: TerrainSurface.BroadNormal), for paved road
varying vec2 road_uv;

// Level-of-detail morph (TerrainLodMesh, TerrainNode.Range): a vertex of level 0 to 3 carries where the next
// coarser level has its point (CUSTOM0.xyz, chunk-local; CUSTOM0.w = level + 1, 0 for none) and that level's
// normal (CUSTOM1.xyz), and slides there over the last stretch of its level's range, by its distance from
// the main camera (the same in the shadow pass), so a chunk has the coarser shape before it changes level.
// (TerrainLodMesh.MorphStart; a coarser level starts only past the finer one's range + TerrainLodMesh.EdgeReach, so no seam opens on a chunk edge)
const float morph_start[4] = { 40.0, 360.0, 730.0, 1800.0 };
const float morph_end[4] = { 70.0, 420.0, 870.0, 2370.0 };   // TerrainLodMesh.Range - TerrainLodMesh.Settle

void vertex() {
	vec2 uv = UV;
	int lvl = int(CUSTOM0.w + 0.5) - 1;
	if (lvl >= 0) {
		vec3 eye = MAIN_CAM_INV_VIEW_MATRIX[3].xyz;
		float d = distance(eye, (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
		float k = clamp((d - morph_start[lvl]) / (morph_end[lvl] - morph_start[lvl]), 0.0, 1.0);
		vec3 move = (CUSTOM0.xyz - VERTEX) * k;
		VERTEX += move;
		uv += vec2(move.x, -move.z) / 5.0;
		NORMAL = normalize(mix(NORMAL, CUSTOM1.xyz, k));
	}
	g_pos = vec3(uv.x * 5.0, VERTEX.y, uv.y * 5.0);
	g_nrm = vec3(NORMAL.x, NORMAL.y, -NORMAL.z);
	g_broad = vec3(CUSTOM2.x, sqrt(max(1.0 - dot(CUSTOM2.xy, CUSTOM2.xy), 0.0)), CUSTOM2.y);
	road_uv = vec2(VERTEX.x, -VERTEX.z) / chunk_units;
}

vec3 noise_decode(vec3 t) { return t * vec3(2.0, 8.0, 8.0) - vec3(1.0, 4.0, 4.0); }

// One octave of the baked value noise (GroundNoise.NoiseTexture: value in -1..1, spread 0.4, and its
// gradient per lattice cell) on a plane: turned by rot (cos, sin), cells `cell` metres wide, shifted by
// offs, read at the mip level the pixel's footprint calls for. Returns the value and its gradient per metre.
const float octave_skip = 24.0;
vec3 octave(vec2 p, float cell, vec2 rot, vec2 offs, float footprint) {
	// far past a pixel's footprint the mip chain has settled to its mean (0): no read (the far ground is most pixels at Far)
	if (footprint > cell * octave_skip) return vec3(0.0);
	vec2 q = vec2(rot.x * p.x - rot.y * p.y, rot.y * p.x + rot.x * p.y) / cell + offs;
	float lod = log2(max(footprint / cell * noise_texels, 1e-4));
	vec3 t = noise_decode(textureLod(noise_tex, q / noise_cells, lod).rgb);
	return vec3(t.x, vec2(rot.x * t.y + rot.y * t.z, -rot.y * t.y + rot.x * t.z) / cell);
}

// The same, read with the pixel's own footprint on the plane (gx, gy: the position's screen derivatives
// there): the mip the footprint calls for, so octaves finer than a pixel settle to their mean.
vec3 octave_g(vec2 p, float cell, vec2 rot, vec2 offs, vec2 gx, vec2 gy) {
	mat2 r = mat2(vec2(rot.x, rot.y), vec2(-rot.y, rot.x));
	float k = 1.0 / (cell * noise_cells);
	vec2 q = (r * p / cell + offs) / noise_cells;
	vec3 t = noise_decode(textureGrad(noise_tex, q, r * gx * k, r * gy * k).rgb);
	return vec3(t.x, transpose(r) * t.yz / cell);
}

// the width a pixel blurs over on a plane after anisotropic filtering (4x, Godot's default)
float filter_width(vec2 gx, vec2 gy) {
	float a = length(gx), b = length(gy);
	return max(max(min(a, b), max(a, b) / 4.0), 1e-4);
}

// cos and sin of i times the golden angle (2.3999632 rad), octave i's turn
const vec2 golden_turn[12] = { vec2(1.0000000, 0.0000000), vec2(-0.7373689, 0.6754903), vec2(0.0874257, -0.9961710), vec2(0.6084389, 0.7936007),
	vec2(-0.9847135, -0.1741818), vec2(0.8437552, -0.5367282), vec2(-0.2596041, 0.9657151), vec2(-0.4609072, -0.8874483),
	vec2(0.9393214, 0.3430384), vec2(-0.9243455, 0.3815567), vec2(0.4238457, -0.9057344), vec2(0.2992842, 0.9541640) };

// the fitted fBm on a plane, in units of its own spread, and its gradient per metre;
// each octave turned by the golden angle against the last, so neither lattices nor tile repeats line up.
// Octaves finer than detail_cell count only `fine` of themselves (the materials stand in for the rest);
// fine_sd gets the share of the spread they would have had.
vec3 ground_noise(vec2 p, vec2 gx, vec2 gy, float fine, float mid_fade, out float fine_sd) {
	float s = 0.0, var = 0.0, var_fine = 0.0, cell = finest_cell;
	float footprint = filter_width(gx, gy);
	vec2 grad = vec2(0.0);
	for (int i = 0; i < 12; i++) {
		if (i >= octaves) break;
		float v = octave_amp[i] * octave_amp[i];
		var += v;
		float keep = smoothstep(0.7, 2.0, cell / footprint);   // finer than a pixel: left out (its mean is 0)
		if (cell < detail_cell) { var_fine += v; keep *= fine; }
		else if (cell < 20.0) keep *= mix(1.0, mid_keep, mid_fade);
		if (keep > 0.0) {
			vec3 n = octave_g(p, cell, golden_turn[i], vec2(float(i) * 17.31, float(i) * 9.7), gx, gy);
			float k = keep * octave_amp[i] / 0.4;
			s += k * n.x;
			grad += k * n.yz;
		}
		cell *= 2.0;
	}
	float sd = sqrt(max(var, 1e-8));
	fine_sd = sqrt(var_fine) / sd;
	return vec3(s, grad) / sd;
}

float lum(vec2 uv) { return dot(texture(ground_tex, uv).rgb, vec3(0.299, 0.587, 0.114)); }
float lum_lin(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

// a normal-map texel (OpenGL: green up) as the height's slope along its texture's u and up
vec2 slope(vec4 nt) {
	vec2 xy = nt.xy * 2.0 - 1.0;
	float z = sqrt(max(1.0 - dot(xy, xy), 0.09));
	return -xy / z;
}

// One material (layer l) at texture coordinates uv (gradients gx, gy), without visible tiling: two reads
// at offsets picked by low-frequency noise k (0..1), blended where the noise crosses between them, by
// height so the seam follows the material. alb: colour, height. Returns the slope along (u, image up);
// rao: roughness, AO.
vec2 sample_untiled(int l, vec2 uv, vec2 gx, vec2 gy, float k, out vec4 alb, out vec2 rao) {
	float lv = k * 8.0;
	float ia = floor(lv), f = fract(lv);
	vec2 oa = sin(vec2(3.0, 7.0) * ia), ob = sin(vec2(3.0, 7.0) * (ia + 1.0));
	// away from the crossing only one of the two is read
	vec4 nt;
	if (f < 0.12) {
		alb = i76_srgb_array(textureGrad(mat_albedo, vec3(uv + oa, float(l)), gx, gy));
		nt = textureGrad(mat_normal, vec3(uv + oa, float(l)), gx, gy);
	} else if (f > 0.88) {
		alb = i76_srgb_array(textureGrad(mat_albedo, vec3(uv + ob, float(l)), gx, gy));
		nt = textureGrad(mat_normal, vec3(uv + ob, float(l)), gx, gy);
	} else {
		vec4 a0 = i76_srgb_array(textureGrad(mat_albedo, vec3(uv + oa, float(l)), gx, gy));
		vec4 a1 = i76_srgb_array(textureGrad(mat_albedo, vec3(uv + ob, float(l)), gx, gy));
		float b = smoothstep(0.25, 0.75, f + (a1.a - a0.a) * 0.8 * smoothstep(0.12, 0.3, min(f, 1.0 - f)));
		alb = mix(a0, a1, b);
		nt = mix(textureGrad(mat_normal, vec3(uv + oa, float(l)), gx, gy), textureGrad(mat_normal, vec3(uv + ob, float(l)), gx, gy), b);
	}
	rao = nt.zw;
	return slope(nt);
}

// A flat material (layer l) on the ground plane, turned by its own angle. Returns the slope in the
// plane's (x, z).
vec2 sample_flat(int l, vec2 p, vec2 dpx, vec2 dpy, float k, out vec4 alb, out vec2 rao) {
	float a = float(l) * 1.9 + 0.4;
	vec2 cs = vec2(cos(a), sin(a));
	mat2 r = mat2(vec2(cs.x, cs.y), vec2(-cs.y, cs.x));
	float t = mat_tile[l];
	vec2 s = sample_untiled(l, r * p / t, r * dpx / t, r * dpy / t, k, alb, rao);
	// image up is -v, which is -z before the turn
	return transpose(r) * (s * vec2(1.0, -1.0));
}

// Rock (layer 3), triplanar: the ground plane as a flat material, the side planes with the texture's
// rows level (its strata run along the cliff) and squashed vertically. k2: the side planes' offset noise.
// Returns the relief as a game-frame gradient; rao: roughness, AO.
vec3 sample_rock(vec3 p, vec3 dpx, vec3 dpy, vec3 w, float k, vec2 k2, out vec4 alb, out vec2 rao) {
	float t = mat_tile[3];
	vec3 g = vec3(0.0);
	alb = vec4(0.0);
	rao = vec2(0.0);
	float ws = 0.0;
	vec4 a; vec2 ra;
	if (w.y > 0.02) {
		vec2 s = sample_flat(3, p.xz, dpx.xz, dpy.xz, k, a, ra);
		alb += a * w.y; rao += ra * w.y; g += vec3(s.x, 0.0, s.y) * w.y; ws += w.y;
	}
	vec2 sq = vec2(1.0, -strata_squash) / t;
	if (w.x > 0.02) {
		vec2 s = sample_untiled(3, p.zy * sq, dpx.zy * sq, dpy.zy * sq, k2.x, a, ra);
		alb += a * w.x; rao += ra * w.x; g += vec3(0.0, s.y * strata_squash, s.x) * w.x; ws += w.x;
	}
	if (w.z > 0.02) {
		vec2 s = sample_untiled(3, p.xy * sq, dpx.xy * sq, dpy.xy * sq, k2.y, a, ra);
		alb += a * w.z; rao += ra * w.z; g += vec3(s.x, s.y * strata_squash, 0.0) * w.z; ws += w.z;
	}
	ws = max(ws, 1e-4);
	alb /= ws; rao /= ws;
	return g / ws;
}

void fragment() {
	vec3 w = pow(abs(normalize(g_nrm)), vec3(triplanar_sharpness));
	w /= (w.x + w.y + w.z);
	vec3 n = normalize(g_nrm);
	float steep = 1.0 - n.y;
	float dist = length(VERTEX);
	vec3 dpx = dFdx(g_pos), dpy = dFdy(g_pos);
	float footprint = max(max(length(dpx), length(dpy)), 1e-4);
	vec4 road_col = has_road ? texture(road_layer, road_uv) : vec4(0.0);
	float road = road_col.a;

	// how much of the near detail is the materials'
	float mat_fade = use_materials && procedural ? 1.0 - smoothstep(mat_near, mat_far, dist) : 0.0;
	float rock_fade = use_materials && procedural ? 1.0 - smoothstep(rock_near, rock_far, dist) : 0.0;

	vec3 ground;
	vec4 gn = vec4(0.0);
	vec3 mgrad = vec3(0.0);          // the materials' relief, a game-frame gradient
	float grain = 0.0, hb = 0.5;     // the materials' luminance (in spreads) and height, for the roads over them
	float rough = 1.0, ao = 1.0, spec = 0.08;   // dirt and rock: no sheen at grazing sun angles
#ifdef PAINTED_GROUND
	{
		// asphalt, laid out as the texture: its base colours (tyre lanes and all) at its own repeat
		vec2 mu = g_pos.xz / repeat_units;
		vec3 base = texture(mark_base, mu).rgb;
		vec3 ratio = vec3(1.0);
		float detail = 0.0;
		vec2 rao = vec2(0.6, 1.0);
		if (mat_fade > 0.0) {
			// the scan's aggregate near the camera: its colour against its mean, so the texture keeps its tone
			float kn = clamp(0.5 + 0.6 * octave(g_pos.xz, 4.3, vec2(0.96, 0.28), vec2(1.1, 4.9), footprint).x, 0.0, 1.0);
			vec4 alb;
			vec2 sl = sample_flat(0, g_pos.xz, dpx.xz, dpy.xz, kn, alb, rao);
			detail = mat_fade * (lum_lin(alb.rgb) - mat_lum_mean.x) / mat_lum_spread.x;
			ratio = mix(vec3(1.0), mix(vec3(1.0), alb.rgb / mat_mean[0], asphalt_detail), mat_fade);
			mgrad = vec3(sl.x, 0.0, sl.y) * mat_bump.x * mat_fade;
			hb = mix(0.5, alb.a, mat_fade);
			rao = mix(vec2(0.6, 1.0), rao, mat_fade);
		} else if (!use_materials) {
			// no scan: fine grain from the noise
			float f = 1.0 - smoothstep(20.0, 60.0, dist);
			if (f > 0.0) {
				detail = f * (0.7 * octave(g_pos.xz, 0.021, vec2(0.6, 0.8), vec2(5.1, 2.7), footprint).x
				            + 0.5 * octave(g_pos.xz, 0.067, vec2(-0.8, 0.6), vec2(1.9, 7.3), footprint).x) / 0.4;
				ratio = vec3(1.0 + 0.12 * detail);
			}
		}
		// broad wear: lighter and darker patches over metres (the scan read large, and noise)
		float broad = 0.6 * octave(g_pos.xz, 7.3, vec2(0.6, -0.8), vec2(2.3, 5.9), footprint).x / 0.4
		            + 0.5 * octave(g_pos.xz, 23.0, vec2(-0.28, 0.96), vec2(8.1, 1.3), footprint).x / 0.4;
		if (use_materials) {
			mat2 r1 = mat2(vec2(0.857, 0.515), vec2(-0.515, 0.857));
			float t1 = mat_tile.x * 11.0;
			vec4 m1 = i76_srgb_array(textureGrad(mat_albedo, vec3(r1 * g_pos.xz / t1, 0.0), r1 * dpx.xz / t1, r1 * dpy.xz / t1));
			broad += 0.6 * (lum_lin(m1.rgb) - mat_lum_mean.x) / mat_lum_spread.x;
		}
		// oil stains: dark, a little glossy, in blotches a metre or so across, scattered
		float o = 0.6 * octave(g_pos.xz, 0.83, vec2(0.28, 0.96), vec2(4.4, 9.2), footprint).x / 0.4
		        + 0.4 * octave(g_pos.xz, 0.29, vec2(-0.96, 0.28), vec2(7.7, 0.4), footprint).x / 0.4
		        + 0.8 * octave(g_pos.xz, 5.9, vec2(0.8, 0.6), vec2(3.3, 6.6), footprint).x / 0.4;
		float oil = oil_stains * smoothstep(1.3, 1.9, o);
		float paint = 0.0;
		vec3 asphalt = base * ratio * (1.0 + lot_wear * broad) * (1.0 - 0.4 * oil);

		// the paint: the field's edge, anti-aliased over the pixel, near; its mipmapped coverage once a line is under a pixel
		float px_texels = footprint / repeat_units * mark_texels;
		float near_k = 1.0 - smoothstep(0.2, 0.5, px_texels);
		if (near_k > 0.0) {
			float d = (textureLod(mark_field, mu, 0.0).r * 2.0 - 1.0) * mark_range;
			float aa = max(length(vec2(dFdx(d), dFdy(d))), 1e-4);
			paint = clamp(0.5 - d / aa, 0.0, 1.0) * near_k;
		}
		if (near_k < 1.0) paint += texture(mark_field, mu).g * (1.0 - near_k);
		// worn: chipped along the lines, thinner in broad patches, the aggregate showing through
		float wn = 0.6 * octave(g_pos.xz, 0.061, vec2(-0.6, 0.8), vec2(6.2, 3.1), footprint).x / 0.4
		         + 0.5 * octave(g_pos.xz, 0.23, vec2(0.96, -0.28), vec2(2.8, 8.8), footprint).x / 0.4
		         + 0.7 * octave(g_pos.xz, 3.7, vec2(-0.28, -0.96), vec2(9.4, 4.6), footprint).x / 0.4;
		paint *= 1.0 - paint_wear * smoothstep(0.3, 1.6, wn);
		vec3 pc = paint_colour * clamp(1.0 + 0.25 * detail, 0.6, 1.2) * (1.0 - 0.35 * oil);
		ground = mix(asphalt, pc, paint);
		grain = detail;
		ao = mix(rao.y, 1.0, 0.5 * paint);
		rough = mix(mix(mix(0.72, 0.95, rao.x), 0.5, oil), 0.62, paint);
		spec = mix(mix(asphalt_shine.y, 0.3, oil), 0.3, paint);
		mgrad *= 1.0 - 0.6 * paint;
	}
#else
	if (!procedural) {
		vec3 top = texture(ground_tex, g_pos.xz / repeat_units).rgb;
		vec3 sx = texture(ground_tex, g_pos.zy / repeat_units).rgb;
		vec3 sz = texture(ground_tex, g_pos.xy / repeat_units).rgb;
		ground = top * w.y + sx * w.x + sz * w.z;
	} else {
		// triplanar like the texture was: the side planes only where they show, blended keeping the spread
		float fine = 1.0 - mat_fade;
		float fs, fine_sd;
		vec3 top = ground_noise(g_pos.xz, dpx.xz, dpy.xz, fine, use_materials ? 1.0 : 0.0, fs);
		gn = vec4(top.x, top.y, 0.0, top.z) * w.y;
		fine_sd = fs * w.y;
		if (w.x > 0.01) { vec3 sx = ground_noise(g_pos.zy, dpx.zy, dpy.zy, fine, use_materials ? 1.0 : 0.0, fs); gn += vec4(sx.x, 0.0, sx.z, sx.y) * w.x; fine_sd += fs * w.x; }
		if (w.z > 0.01) { vec3 sz = ground_noise(g_pos.xy, dpx.xy, dpy.xy, fine, use_materials ? 1.0 : 0.0, fs); gn += vec4(sz.x, sz.y, sz.z, 0.0) * w.z; fine_sd += fs * w.z; }
		gn /= sqrt(dot(w, w));
		float z = gn.x;
		vec3 chroma = vec3(1.0);
		vec4 dbg = vec4(0.0);

		if (use_materials) {
			// where each material wants to be (0..1): broad noise for variety, slope, hollows
			// the patches' coordinates warped by broader noise, so their edges flow rather than blob
			// (one read: the warp is a broad octave's gradient, which swirls)
			vec2 wp = g_pos.xz + 22.0 * 53.0 * octave(g_pos.xz, 53.0, vec2(0.96, -0.28), vec2(3.7, 9.1), footprint).yz;
			float v1f = octave(wp, 6.7, vec2(-0.6, 0.8), vec2(1.3, 6.1), footprint).x;
			float v1 = (0.8 * octave(wp, 29.0, vec2(0.6, 0.8), vec2(5.3, 1.7), footprint).x + 0.35 * v1f) / 0.4;
			float v2 = (0.8 * octave(wp, 71.0, vec2(-0.8, 0.6), vec2(2.9, 8.1), footprint).x
			          + 0.35 * octave(wp, 15.3, vec2(0.8, -0.6), vec2(9.9, 4.4), footprint).x) / 0.4;
			float v3 = octave(g_pos.xz, 13.0, vec2(0.28, -0.96), vec2(7.7, 3.3), footprint).x / 0.4;
			// macro texture: two of the materials read again far larger, their luminance as broad variation
			// with the look of real ground rather than of noise (mipmapped, filtered anisotropically)
			float macro = 0.0;
			{
				mat2 r1 = mat2(vec2(0.857, 0.515), vec2(-0.515, 0.857));
				mat2 r2 = mat2(vec2(-0.416, 0.909), vec2(-0.909, -0.416));
				float t1 = mat_tile[2] * macro_scale.x, t2 = mat_tile[0] * macro_scale.y;
				vec4 m1 = i76_srgb_array(textureGrad(mat_albedo, vec3(r1 * g_pos.xz / t1, 2.0), r1 * dpx.xz / t1, r1 * dpy.xz / t1));
				vec4 m2 = i76_srgb_array(textureGrad(mat_albedo, vec3(r2 * g_pos.xz / t2, 0.0), r2 * dpx.xz / t2, r2 * dpy.xz / t2));
				macro = 0.6 * (lum_lin(m1.rgb) - mat_lum_mean[2]) / mat_lum_spread[2] + 0.4 * (lum_lin(m2.rgb) - mat_lum_mean[0]) / mat_lum_spread[0];
			}
			float hollow = 0.0;
			if (has_cavity) hollow = textureLod(cavity_tex, (g_pos.xz / 5.0 - chunk_origin + 1.5) / cavity_texels, 0.0).r - 0.5;
			float flat_ = 1.0 - smoothstep(0.04, 0.16, steep);
			float want[4];
			want[0] = 1.0;
			want[1] = smoothstep(0.25, 0.9, v2 + 0.3 * v3) * flat_ * (1.0 - smoothstep(0.0, 0.08, hollow));
			want[2] = clamp(smoothstep(0.02, 0.12, hollow + 0.03 * v3) + smoothstep(0.5, 1.2, -v1 + 0.4 * v3) * 0.7
			              + smoothstep(0.1, 0.3, steep) * (1.0 - smoothstep(0.3, 0.5, steep)) * 0.5, 0.0, 1.0);
			want[3] = smoothstep(rock_slope_lo, rock_slope_hi, steep + 0.06 * v1 - 0.4 * max(hollow, 0.0));
			// low-frequency noise for the flat materials' offsets
			float kn = clamp(0.5 + 0.6 * octave(g_pos.xz, 4.3, vec2(0.96, 0.28), vec2(1.1, 4.9), footprint).x, 0.0, 1.0);
			vec2 kside = vec2(0.5);
			if (want[3] > 0.01 && rock_fade > 0.0)
				kside = clamp(0.5 + 0.6 * vec2(octave(g_pos.zy, 7.1, vec2(1.0, 0.0), vec2(4.1, 2.2), footprint).x,
				                               octave(g_pos.xy, 7.1, vec2(1.0, 0.0), vec2(6.3, 1.9), footprint).x), 0.0, 1.0);

			// top-down height blend: each layer over the ones below, its high points first. Each layer's
			// own detail counts as far as it is read (fl: the flat ones near, rock further out).
			float left = 1.0, wsum = 0.0;
			float tone = 0.0, detail = 0.0, r_acc = 0.0, ao_acc = 0.0, h_acc = 0.0;
			vec3 ratio = vec3(0.0), grad = vec3(0.0);
			for (int l = 3; l >= 0; l--) {
				if (left < 0.01) break;
				float wl = want[l];
				if (l > 0 && wl < 0.01) continue;
				float fl = l == 3 ? rock_fade : mat_fade;
				vec4 alb = vec4(0.5);
				vec2 rao = vec2(0.9, 1.0);
				vec3 g = vec3(0.0);
				if (fl > 0.0) {
					if (l == 3) g = sample_rock(g_pos, dpx, dpy, w, kn, kside, alb, rao);
					else { vec2 s = sample_flat(l, g_pos.xz, dpx.xz, dpy.xz, kn, alb, rao); g = vec3(s.x, 0.0, s.y); }
				}
				float h = mix(0.5, alb.a, fl);
				float a = l == 0 ? 1.0 : clamp((h + 2.0 * wl - 1.5) * blend_contrast + 0.5, 0.0, 1.0);
				float wt = left * a;
				left *= 1.0 - a;
				wsum += wt;
				if (l == 1) dbg.r = wt; if (l == 2) dbg.g = wt; if (l == 3) dbg.b = wt;
				tone += wt * mat_tone[l];
				float L = lum_lin(alb.rgb);
				vec3 hue = clamp((alb.rgb / max(L, 1e-3)) / (mat_mean[l] / max(mat_lum_mean[l], 1e-3)), vec3(0.5), vec3(2.0));
				detail += wt * fl * (L - mat_lum_mean[l]) / mat_lum_spread[l];
				ratio += wt * mix(vec3(1.0), hue, fl);
				grad += wt * fl * g * mat_bump[l];
				r_acc += wt * mix(0.5 * (ground_rough.x + ground_rough.y), mix(ground_rough.x, ground_rough.y, rao.x), fl);
				ao_acc += wt * mix(1.0, rao.y, fl);
				h_acc += wt * h;
			}
			wsum = max(wsum, 1e-4);
			// strata: on rock, the palette rank banded by height (layers level across the map)
			float sp = dot(g_pos.xz, vec2(0.6, 0.8)) * 0.02;
			float strata = want[3] < 0.01 ? 0.0 : octave(vec2(g_pos.y / 1.7, sp), 1.0, vec2(1.0, 0.0), vec2(3.1, 0.7), footprint / 1.7).x / 0.4
			             + 0.6 * octave(vec2(g_pos.y / 5.3, sp * 0.5), 1.0, vec2(1.0, 0.0), vec2(9.7, 2.3), footprint / 5.3).x / 0.4;
			z += macro_gain * macro * w.y + (tone + detail_gain * fine_sd * detail) / wsum + want[3] * strata_amp * strata;
			chroma = mix(vec3(1.0), ratio / wsum, chroma_keep);
			mgrad = grad / wsum;
			grain = detail / wsum;
			hb = h_acc / wsum;
			rough = r_acc / wsum;
			ao = ao_acc / wsum;
			spec = ground_specular;
		}
		// the noise's value as a rank (a normal CDF), the rank as a colour
		float rank = 0.5 + 0.5 * tanh(0.7978846 * (z + 0.044715 * z * z * z));
		ground = texture(palette, vec2(mix(0.5 / 32.0, 31.5 / 32.0, rank), 0.5)).rgb * chroma;
		if (debug_view == 1) ground = dbg.rgb;
	}
#endif
#ifndef PAINTED_GROUND
	// the sward: a gentle shift toward dry grass where the grasses are dense (the map, smooth over its 4 m texels),
	// varied broadly (tens of metres, no edges) and nothing finer, so it never reads as patches near the camera
	if (i76_sward_tint.a > 0.0) {
		float d = texture(i76_sward, (g_pos.xz - i76_sward_rect.xy) * i76_sward_rect.zw).r * i76_sward_tint.a;
		d *= 1.0 - smoothstep(0.25, 0.5, steep);
		if (d > 0.002) {
			float broad = octave(g_pos.xz, 23.0, vec2(0.34, 0.94), vec2(5.1, 2.9), footprint).x / 0.4;
			float cov = clamp(d * (1.0 + 0.3 * broad), 0.0, 1.0);
			// the dry grass at the ground's own lightness, mostly: the shift is in hue more than in tone
			vec3 grass = i76_sward_tint.rgb * mix(vec3(1.06, 1.0, 0.86), vec3(0.94, 1.0, 0.96), smoothstep(0.3, 0.9, d));
			grass *= mix(1.0, lum_lin(ground) / max(lum_lin(grass), 1e-3), sward_keep_lum);
			ground = mix(ground, grass, cov * sward_cover);
			rough = mix(rough, 1.0, cov * 0.5);
			spec = mix(spec, 0.04, cov * 0.5);
		}
	}
#endif
	float road_relief = 0.15;    // how much of the ground's relief shows through the road
	float paved = 0.0;           // how much of the ground is paved road (asphalt): laid flat, with none of the ground's bumps
	if (has_road) {
		// asphalt (grey) against dirt tracks (coloured)
		float sat = max(road_col.r, max(road_col.g, road_col.b)) - min(road_col.r, min(road_col.g, road_col.b));
		float dirt = smoothstep(0.05, 0.2, sat);
		// the edges broken up by the ground's height (its high points poke through)
		road = clamp(road - (hb - 0.5) * road_edge * 4.0 * road * (1.0 - road), 0.0, 1.0);
		// the ground's grain carried into the road near the camera: aggregate in asphalt, ruts and stones in dirt
		vec3 rc = road_col.rgb * mix(asphalt_tone, 1.0, dirt) * clamp(1.0 + grain * mix(road_grain.x, road_grain.y, dirt), 0.6, 1.4);
		ground = mix(ground, rc, road);
		rough = mix(rough, mix(asphalt_shine.x, dirt_road_shine.x, dirt), road);
		spec = mix(spec, mix(asphalt_shine.y, dirt_road_shine.y, dirt), road);
		ao = mix(ao, mix(1.0, ao, 0.5 * dirt), road);
		road_relief = mix(0.25, 0.6, dirt);
		paved = road * (1.0 - dirt);
	}
	ROUGHNESS = rough;
	SPECULAR = spec;
	ALBEDO = ground;
	{
		vec4 so = surface_overlay(g_pos.xz);
		ALBEDO = mix(ALBEDO, so.rgb, so.a);
		EMISSION += so.rgb * so.a * 0.3;
		vec2 bc = brush_cursor(g_pos.xz);
		ALBEDO = mix(ALBEDO, i76_brush_style.rgb, clamp(bc.x * 0.85 + bc.y * 0.22, 0.0, 1.0));
		EMISSION += i76_brush_style.rgb * (bc.x * 0.7 + bc.y * 0.1);
	}
	if (debug_view == 2) ALBEDO = vec3(0.35);   // the light alone, on a grey ground (against the plants' I76_VEG_DEBUG=1)
	if (use_materials && procedural) { AO = ao; AO_LIGHT_AFFECT = 0.35; }

	// ---- the relief ----
	float fine = 1.0 - smoothstep(bump_far * 0.3, bump_far, dist);
	float far = 1.0 - smoothstep(bump_far, bump_far * 4.0, dist);
	// the relief's gradient: broad undulation, mid lumps, fine grit (in metres; the fine octaves fade with
	// distance, and near the camera with the materials, which carry their own)
	float grit = fine * (1.0 - mat_fade);
	vec2 rg = octave(g_pos.xz, 9.09, vec2(0.8, 0.6), vec2(3.1, 7.7), footprint).yz * 0.3
	        + octave(g_pos.xz, 1.667, vec2(-0.28, 0.96), vec2(11.3, 5.9), footprint).yz * 0.1 * fine;
#ifdef PAINTED_GROUND
	rg *= lot_relief;   // a lot is laid flat
	grit *= 0.3;
#endif
	if (grit > 0.0) rg += octave(g_pos.xz, 0.370, vec2(0.6, -0.8), vec2(23.9, 41.3), footprint).yz * 0.025 * grit;
	vec3 grad = vec3(rg.x, 0.0, rg.y) * bump_noise;
	grad *= mix(0.6, 1.8, steep) * (1.0 - 0.7 * road);
	// the texture as height on the top projection: brightness differences one texel apart
	vec2 uv = g_pos.xz / repeat_units;
	float t = 1.0 / float(textureSize(ground_tex, 0).x);
	float tx = 0.0, tz = 0.0;
	if (!procedural) {
		tx = (lum(uv + vec2(t, 0)) - lum(uv - vec2(t, 0))) / (2.0 * t * repeat_units);
		tz = (lum(uv + vec2(0, t)) - lum(uv - vec2(0, t))) / (2.0 * t * repeat_units);
	}
	// the ground's brightness as height: the noise's own gradient, or the texture's on its top projection
	vec3 tgrad = procedural ? gn.yzw * palette_slope * 0.3989423 * exp(-0.5 * gn.x * gn.x) : vec3(tx, 0.0, tz) * w.y;
	grad += tgrad * bump_texture * fine * (1.0 - 0.5 * road);
	grad += mgrad * mix(1.0, road_relief, road);
	grad *= far * (1.0 - paved);   // asphalt keeps the ground's slope but not its bumps
	// and takes that slope over a broad span: the samples' own lumps, a few metres each, read as a bumpy track at night
	n = normalize(mix(n, normalize(g_broad), paved));
	// tilt the normal against the gradient's part in the surface, then back to Godot's frame and view space
	vec3 bn = normalize(n - (grad - dot(grad, n) * n));
	vec3 world_n = vec3(bn.x, bn.y, -bn.z);
	NORMAL = normalize((VIEW_MATRIX * vec4(world_n, 0.0)).xyz);
}
