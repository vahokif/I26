# The Compatibility renderer (a GPU without Vulkan or D3D12)

I26 is made for Godot's Forward+ renderer, which runs on Vulkan (Linux, Windows) or Direct3D 12 (Windows). A machine
whose driver has neither (an old GPU, a virtual machine, Wine without Vulkan) makes Godot fall back to its
**Compatibility** renderer, on OpenGL 3.3 / OpenGL ES 3. The game runs there, plainer than it should and a little
differently. The log's second line says which renderer is in use (`renderer: forward_plus (...)` or `gl_compatibility`),
and in Compatibility the shell shows a notice once a run.

Force it for a test with `--rendering-method gl_compatibility --rendering-driver opengl3` (before the `--`), or
`I76_SHOT_GL=1 tools/shot.sh ...` for a capture; `I76_SHADER_SMOKE=1` draws every shader once and quits, so the log shows
any the renderer refuses.

## What Compatibility lacks, and what the game does

| Lacking | What the game does |
|---|---|
| `textureQueryLevels` (a high-end feature) | The 1997 Software look's point-sampled mip level took the texture's mip count from it; there the count comes from the texture's size (every mipmapped texture here has a full chain). Before this, the 1997 ground shader failed to compile (an error at every mission start, and no terrain in the 1997 look). |
| SSAO, SSR, SDFGI, volumetric fog | Left off (Shadow Detail High is Low there). The environment's settings for them are ignored. |
| sRGB decoding of array textures, and of a sky shader's textures | Done by hand in the shaders that read them (the terrain's material arrays, the sky's clouds, sprite and horizon panels), through `compat.gdshaderinc` and `sky_modern.gdshaderinc`. |
| The sky pass's linear-to-sRGB encode | The modern sky shader encodes its colour itself (not in the radiance pass, which is the ambient light). |
| sRGB handling under the 1997 look's Linear tonemap | The 1997 shaders decode their run-time textures and encode their colour by hand (`i76_tex`, `i76_out` in `compat.gdshaderinc`); without it the whole 1997 look was dark and M15's parking lot black. |
| Decals | The modern look's oil ribbons and fire scorches (`GroundMark`) are meshes laid on the drawn ground there, matte, the scorch glowing a flat ember colour. Before this you slid on oil you could not see. |
| A reversed depth buffer (its clip z runs -1..1) | The soft-edged effects (dropped fire, wreck flames, smoke, napalm, muzzle flash) turn a depth-texture read back into clip space with `i76_ndc_depth` (`compat.gdshaderinc`). Before this they all faded to nothing. The fog pass (`sky_fog`) and the headlight glare still read it the Forward+ way. |
| A mesh's `COLOR` read right beside custom vertex data | The plants' vertex colour is also sent as `CUSTOM1`, which the vegetation shader reads there. |

None of this changes what Forward+ draws: every workaround is inside `#if CURRENT_RENDERER == RENDERER_COMPATIBILITY` (or a
run-time check of `RenderBackend.Compatibility`), and captures of Forward+ before and after agree but for the clouds'
drift.

## What is still different

Measured against the same frames on Forward+ (RMSE of a 960x540 capture: 3-4% in the 1997 look, 2-8% in the modern look):

- Shadows, the sun and the plants are there; the car's paint reflects the sky a little more (the hood's stripes read grey).
- Roads and the M15 lot are a little paler or pinker in the modern look: a run-time texture's decode differs by shader and
  not every one is corrected.
- No SSAO, so Shadow Detail High looks like Low.
- Colour management is by hand, so a new shader that reads a run-time texture or writes the 1997 frame must use
  `i76_tex` / `i76_out` or it will look dark or washed out only in Compatibility. Run `I76_SHADER_SMOKE=1` on both
  renderers when adding a shader, and compare a capture of each (`I76_SHOT_GL=1`).
- Checked in a fight: the dropped oil and fire. Not checked: other effects (smoke, tracers) beyond that they compile; the movies; the Nitro maps one by one.

## System requirements

A GPU with Vulkan 1.2 (or, on Windows, Direct3D 12) is the target. Anything with OpenGL 3.3 or newer runs in
Compatibility, with the differences above.
