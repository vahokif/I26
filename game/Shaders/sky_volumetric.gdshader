// The modern sky with volumetric clouds, marched in the half-resolution sky pass (a quarter of the pixels): Graphic Detail's
// Clouds: Volumetric. See sky_volumetric.gdshaderinc.
shader_type sky;
render_mode use_half_res_pass;
#define I76_VOL_PASS AT_HALF_RES_PASS
#define I76_VOL_COLOR HALF_RES_COLOR
#include "sky_volumetric.gdshaderinc"
