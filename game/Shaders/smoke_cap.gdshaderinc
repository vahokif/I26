// The smoke layer's opacity cap (smoke_layer.gdshader, headlight_glare.gdshader): the accumulated alpha of all the puffs
// along a pixel (0..1) goes through a soft minimum with i76_smoke_cap (the identity well below it, a knee about it, the cap itself far above), so
// thin smoke is as it was and a thick column levels off at the cap instead of going solid.
// i76_smoke_cap is 0 while the layer is not in use; i76_smoke holds, per pixel, the layer's mean smoke distance premultiplied
// by its alpha (r) and the alpha (a).
#ifndef I76_SMOKE_CAP_INC
#define I76_SMOKE_CAP_INC
global uniform float i76_smoke_cap;
float i76_smoke_alpha(float a) { float c = max(i76_smoke_cap, 1e-3), r = a / c, r2 = r * r; return a / pow(1.0 + r2 * r2 * r2, 1.0 / 6.0); }
#endif
