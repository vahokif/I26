// The shade a plant throws on the ground under it from the sky (World/Vegetation.cs, docs/modern-vegetation.md):
// one quad a shrub (and a larger ground plant), laid on the ground's slope, multiplying what is drawn there darker
// toward the middle, as the cars' drop shadows ground them, and darker still right at its foot. Per instance
// (INSTANCE_CUSTOM): x the share of the sky's light lost at the foot (PlantShading.GroundShade). The quad is pulled
// toward the camera a little so the drawn ground (whose level of detail can lie a few centimetres above the plant's
// base) never hides it; it fades out with distance before the terrain's coarser levels move the ground under it.
shader_type spatial;
render_mode blend_mul, unshaded, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled, world_vertex_coords;

uniform float strength = 0.85;
// the darkening right at the foot, where any plant meets the ground (its share of the quad, and how dark)
uniform vec2 contact = vec2(0.35, 0.3);
uniform float fade_start = 50.0;
uniform float fade_end = 70.0;

varying float v_shade;
varying float v_fade;
varying vec2 v_uv;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float d = distance(origin, MAIN_CAM_INV_VIEW_MATRIX[3].xyz);
	v_fade = 1.0 - smoothstep(fade_start, fade_end, d);
	v_shade = INSTANCE_CUSTOM.x * strength * v_fade;
	v_uv = UV * 2.0 - 1.0;
	vec3 to_eye = MAIN_CAM_INV_VIEW_MATRIX[3].xyz - VERTEX;
	VERTEX += normalize(to_eye) * (0.12 + 0.004 * d);
}

void fragment() {
	float r = length(v_uv);
	// dark under the crown, easing out past its rim
	float k = 1.0 - smoothstep(0.3, 1.0, r);
	float foot = contact.y * v_fade * (1.0 - smoothstep(0.0, contact.x, r));
	ALBEDO = vec3(1.0 - min(0.9, v_shade * k + foot));
}
