// The burning scenery wrecks' fire (StaticFire): a few upright billboards over the wreck, each a procedural fire.
// Per quad (StaticFire.FireMesh): VERTEX its base centre, UV its corner (y up), UV2 its width and height (m), COLOR.r a
// seed, COLOR.g the share of its height that is soot above the flames. The flames are noise scrolling upward, warped
// sideways more toward the top, cut by a body that tapers to the tips: tongues that lick up and break off. A
// temperature ramp from the cut (deep red edges, orange, a yellow-white core low down), HDR in the core so it glows
// through the tonemapper. Premultiplied: mostly laid over the scene, so it reads on bright snow, plus an added halo
// for the night. Above the flames of the tallest quad, soot: dark, dense at the tips, lit orange from beneath, so
// DamageSmoke's column rises out of the fire instead of sitting on it.
shader_type spatial;
#include "res://Shaders/cab_clip.gdshaderinc"
#include "res://Shaders/compat.gdshaderinc"
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_premul_alpha, fog_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
// DamageSmoke's baked tiling fbm (R, G: two fields; 8 cells to a tile)
uniform sampler2D noise_tex : repeat_enable, filter_linear;
uniform float soft_distance = 0.6;
uniform float reach = 130.0;          // gone at this distance, fading over its last fifth
uniform float scene_light = 1.0;      // the mission's light: the soot darker at night, the halo stronger

varying vec2 v_size;
varying float v_seed;
varying float v_soot;
varying float v_fade;

// the fbm, in cells, stretched to about 0..1
float nr(vec2 p) { return clamp((texture(noise_tex, p * 0.125).r - 0.5) * 2.6 + 0.5, 0.0, 1.0); }
float ng(vec2 p) { return clamp((texture(noise_tex, p * 0.125).g - 0.5) * 2.6 + 0.5, 0.0, 1.0); }

void vertex() {
	v_size = UV2;
	v_seed = COLOR.r + fract(dot(NODE_POSITION_WORLD.xz, vec2(0.0137, 0.0071)));
	v_soot = COLOR.g;
	// turned to the camera about world up (the node is never rotated or scaled: model space is world space moved)
	vec3 to = CAMERA_POSITION_WORLD - NODE_POSITION_WORLD - VERTEX;
	vec2 tx = to.xz;
	vec3 side = dot(tx, tx) > 1e-4 ? normalize(vec3(tx.y, 0.0, -tx.x)) : vec3(1.0, 0.0, 0.0);
	VERTEX += side * (UV.x - 0.5) * UV2.x + vec3(0.0, UV.y * UV2.y, 0.0);
	float dist = length(CAMERA_POSITION_WORLD - (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
	// out of reach fades; and so does a quad the camera is about to pass through (it would fill the view, the mirror's too)
	v_fade = (1.0 - smoothstep(reach * 0.8, reach, dist)) * smoothstep(1.5, 4.0, length(to));
}

void fragment() {
	CAB_CLIP
	float w = v_size.x, hq = v_size.y;
	float fh = hq * (1.0 - v_soot);                  // the flames' height on this quad
	float xm = (UV.x - 0.5) * w, ym = UV.y * hq;   // metres from the base centre
	float yf = ym / fh;                               // 0 at the base, 1 at the flames' top
	float t = TIME;
	vec2 s = vec2(v_seed * 37.0, v_seed * 11.0);
	// sideways sway, slow and large, more toward the top
	float sway = (ng(vec2(xm * 0.12, ym * 0.10 - t * 0.35) + s) - 0.5) * 1.6;
	float xw = xm + sway * min(ym, fh) * 0.3;
	// a finer warp, so the edges curl instead of running straight
	float wq = ng(vec2(xm * 0.9, ym * 0.45 - t * 1.9) + s * 0.7) - 0.5;
	xw += wq * (0.25 + 0.6 * min(yf, 1.0));
	float yw = ym + wq * 0.6;
	// the tongues: tall features rising fast, and finer licks faster still
	float n1 = nr(vec2(xw * 0.6, yw * 0.24 - t * 1.3) + s);
	float n2 = ng(vec2(xw * 1.4, yw * 0.55 - t * 2.5) + s * 1.7);
	float n3 = nr(vec2(xw * 3.0, yw * 1.2 - t * 4.0) + s * 2.3);
	float n = n1 * 0.55 + n2 * 0.30 + n3 * 0.15;
	// the body: wide and full at the base, tapering to the tips
	float yc = clamp(yf, 0.0, 1.3);
	float halfw = 0.5 * w * mix(0.9, 0.2, pow(min(yc, 1.0), 0.8));
	float xd = abs(xw) / halfw;
	float body = 1.0 - xd * xd;
	// the noise decides more of the shape the higher it goes: whole tongues low, licks breaking off high
	float heat = body * 0.75 - yc * 0.5 + (n - 0.5) * (0.9 + 1.7 * yc) + 0.12 - (1.0 - n3) * 0.35 * yc;
	heat *= smoothstep(0.0, 0.07, yf);                  // the foot sits in the wreck
	// never reaching the quad's sides or top
	heat -= smoothstep(0.3, 0.48, abs(xm) / w) * 2.0 + smoothstep(0.8, 1.0, ym / fh) * 0.8 + smoothstep(0.85, 1.0, ym / hq);
	float a = smoothstep(-0.02, 0.26, heat);
	// hotter low and in the middle, cooler out at the edges and the tips, mottled inside
	float temp = clamp((heat * 1.3 - (1.0 - n3) * 0.22) * (1.1 - 0.5 * yc), 0.0, 1.4);
	// deep red, orange, yellow, white-yellow
	vec3 col = mix(vec3(0.42, 0.035, 0.004), vec3(0.95, 0.2, 0.015), smoothstep(0.0, 0.3, temp));
	col = mix(col, vec3(1.0, 0.48, 0.05), smoothstep(0.25, 0.65, temp));
	col = mix(col, vec3(1.0, 0.72, 0.22), smoothstep(0.65, 1.05, temp));
	col = mix(col, vec3(1.0, 0.92, 0.7), smoothstep(1.1, 1.4, temp));
	// a flicker in the whole, and HDR toward the core
	float flick = 0.85 + 0.3 * nr(vec2(t * 2.3, v_seed * 13.0));
	col *= (1.0 + 0.9 * smoothstep(0.55, 1.3, temp)) * flick;

	// soot over the tallest one's flames: billows rising slower, dense at the tips, thinning toward the top
	vec3 sc = vec3(0.0);
	float sa = 0.0;
	if (v_soot > 0.0) {
		float ys = (ym - fh * 0.6) / (hq - fh * 0.6);   // 0 inside the upper flames, 1 at the quad's top
		// rolling billows: warped, slower than the flames
		float sq = ng(vec2(xm * 0.35, ym * 0.22 - t * 0.5) + s * 2.0) - 0.5;
		vec2 sp = vec2(xm + sq * 1.4 + sway * 0.5, ym - t * 0.9);
		float sn = nr(sp * vec2(0.5, 0.42) + s * 3.0) * 0.6 + ng(sp * 1.2 + s) * 0.4;
		float sw = 0.5 * w * mix(0.3, 0.6, clamp(ys, 0.0, 1.0));
		float sb = 1.0 - pow(abs(sp.x) / sw, 2.0);
		float sd = sb * 0.8 + (sn - 0.5) * 2.4 - ys * 0.35;
		sa = smoothstep(0.0, 0.35, sd) * smoothstep(0.0, 0.15, ys) * (1.0 - smoothstep(0.55, 1.0, ys)) * 0.95;
		// a little warm from the fire beneath, charcoal above, lumpy
		vec3 lit = mix(vec3(0.06, 0.028, 0.012), vec3(0.03, 0.028, 0.026), smoothstep(0.05, 0.4, ys)) * (0.4 + 0.6 * scene_light);
		sc = lit * (0.7 + 0.6 * sn);
	}

	float depth = texture(depth_tex, SCREEN_UV).r;
	vec4 vp = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, i76_ndc_depth(depth), 1.0);
	float soft = clamp((-vp.z / vp.w + VERTEX.z) / soft_distance, 0.0, 1.0) * v_fade;
	a *= 0.95;
	// flames over soot (premultiplied)
	vec3 rgb = col * a + sc * sa * (1.0 - a);
	float alpha = a + sa * (1.0 - a);
	// the halo: a little light added around the flames, more in the dark
	float halo = clamp(heat + 0.35, 0.0, 1.0) * (1.0 - a) * mix(0.3, 0.0, clamp(scene_light, 0.0, 1.0));
	rgb += vec3(1.0, 0.4, 0.1) * halo * halo * (1.0 - sa);
	if (alpha < 0.003 && halo < 0.003) discard;
	ALBEDO = rgb * soft;
	ALPHA = alpha * soft;
}
