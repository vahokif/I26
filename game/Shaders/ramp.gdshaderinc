// The engine's light ramp (the mission's .act and .lum): a vertex lit `light` takes LUMA[round(31*light)], as (a, b):
// the 3D card's ramp is a alone (b 0), the Software renderer's the .lum row's fit colour*a + b (software.gdshaderinc).
// Held in 16 plain global uniforms, two entries each (xy, zw), not a texture: a vertex-stage fetch from a global sampler
// draws the terrain black on some drivers (AMD's Windows Vulkan, #133). Set by ObjectLight.SetRamp.
global uniform vec4 i76_luma_0;
global uniform vec4 i76_luma_1;
global uniform vec4 i76_luma_2;
global uniform vec4 i76_luma_3;
global uniform vec4 i76_luma_4;
global uniform vec4 i76_luma_5;
global uniform vec4 i76_luma_6;
global uniform vec4 i76_luma_7;
global uniform vec4 i76_luma_8;
global uniform vec4 i76_luma_9;
global uniform vec4 i76_luma_10;
global uniform vec4 i76_luma_11;
global uniform vec4 i76_luma_12;
global uniform vec4 i76_luma_13;
global uniform vec4 i76_luma_14;
global uniform vec4 i76_luma_15;
vec2 i76_ramp(float light) {
	vec4 t[16] = vec4[16](i76_luma_0, i76_luma_1, i76_luma_2, i76_luma_3, i76_luma_4, i76_luma_5, i76_luma_6, i76_luma_7, i76_luma_8, i76_luma_9, i76_luma_10, i76_luma_11, i76_luma_12, i76_luma_13, i76_luma_14, i76_luma_15);
	int k = int(round(31.0 * clamp(light, 0.0, 1.0)));
	vec4 p = t[k >> 1];
	return (k & 1) == 0 ? p.xy : p.zw;
}
