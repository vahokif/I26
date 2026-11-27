// The modern sky with volumetric clouds, marched in the quarter-resolution sky pass (a sixteenth of the pixels; I76_CLOUDS=quarter,
// an A/B of the cost). See sky_volumetric.gdshaderinc.
shader_type sky;
render_mode use_quarter_res_pass;
#define I76_VOL_PASS AT_QUARTER_RES_PASS
#define I76_VOL_COLOR QUARTER_RES_COLOR
#include "sky_volumetric.gdshaderinc"
