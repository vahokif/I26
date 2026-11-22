# Modern look: shadows from the clouds

The modern look's sky is the mission's own cloud sheet, drawn as a dome and scrolled with time (docs and
`sky_modern.gdshaderinc`). Nothing was under it: the ground, the roads and every car in a mission were lit by
the day's one light setting whatever hung overhead, so a day with a heaped sky looked like a bright one and a
shadow could not be seen anywhere but under a bridge. Now the sheet casts. Each pixel of the world is slid up
the sun onto the dome and takes the sun out of itself wherever the sheet shows cloud, so a shade stands under
the cloud that hangs over it, edges soft, and the two drift together as the sheet scrolls. It is one texture
fetch, no geometry, no extra light and no shadow map, and it reaches the whole extended terrain out to the
distant ring — which is the reason it is done this way rather than by casting the clouds.

| Where | What |
|---|---|
| `game/Shaders/cloud_shadow.gdshaderinc` | the maths: a point's own cloud, the sky's fill to put back, and the globals carrying both |
| `game/Scripts/World/CloudShadow.cs` | the policy: the sheet and its dome and levels off the mission's sky material, the strength by day and night, the sky's fill, the map origin, the look |
| `game/Shaders/terrain_modern.gdshaderinc` | the ground and its lots (`terrain_lot.gdshader` shares it) |
| `game/Shaders/road.gdshader` | the roads, in its modern branch only |
| `game/Shaders/object.gdshaderinc` | cars and scenery, in the modern branch of its `fragment()` |
| `game/Shaders/vegetation.gdshader` | the plants, their backlight with them |
| `game/Shaders/horizon_terrain.gdshaderinc` | the distant terrain and the chunks past the map, through its own `light()` |
| `game/project.godot` | `[shader_globals]` entries for the nine globals: a `global uniform` in a shader is not registered by itself, and a push to an unregistered name is dropped |
| `game/Scripts/Boot/Main.cs` | `ApplySkySteps` starts it with the sky, `ApplyGraphics` takes the two menu settings, `Rebase` moves the origin, `SetMode1997Steps` turns it off |
| `game/Scripts/Shell/Overlays.cs` | the front end's Graphic Detail: Cloud Shadows (On/Off) and Cloud Speed (Normal/Half/Quarter) |
| `game/Scripts/Ui/EscapeMenu.cs` | the same two rows on the pad inside a mission, which is its own copy of that list, and Reset's back to them |
| `game/Scripts/Shell/Shell.cs` | the two settings on `ShellSettings`, and shell.cfg's `[port] cloud_shadows` / `cloud_speed` behind them |
| `game/Scripts/Shell/Garage3D/GarageAssets.cs` | off in the 3D garage, and back with the mission's globals when it goes |

## Where the point's cloud is

`modern_sky` meets the eye's ray with a sphere of `radius` about a centre `drop` below the eye and takes the
intersection's XZ as the sheet's UV: `uv = (xz / radius + 0.5) * 4 + scroll`, in the game frame (the sky mirrors
Godot's Z to get it, and the shadow mirrors it back the same way, four tiles across the sheet). A cloud is a
long way up, so the ground under it is not in the direction the cloud is seen but a little the way the sun is:
for a sheet `radius - drop` over the ground — 600 m on the mission's numbers — a point `p` lies under the cloud
at

```
k = (600 - p.y) / max(sun.y, 0.15)          // how far up the sun, and how far along the ground
uv = ((p.xz + sun.xz * k) / radius + 0.5) * 4 + TIME * scroll_per_second
```

`sun` is the direction to the sun in Godot's frame, as `i76_light_dir` has it, and `TIME * scroll_per_second`
is the sky's own scroll, so a shadow is under its cloud and moves with it. The sheet's and the dome's numbers
are read off the modern sky's material by `CloudShadow.Configure` rather than written again here, and the
threshold between open sky and cloud is the mission's measured `sky_lum` and `cloud_lum`
(`MissionBuilder.MeasureSky`), so the shade agrees with what the sky shows instead of with a guess.

Three things follow. The pattern is anchored to the map, not to the eye: `Rebase` moves the Godot origin under a
long trip, and `i76_cloud_anchor` adds it back, so a shadow does not slide sideways when it does. The projection
is a plane, not the sphere, which is right close under and wrong near the horizon, where a cloud is drawn a long
way from where it would cast; the fade of the strength by the sun's height (`smoothstep(0.08, 0.3, sun.y)`)
covers the low-sun case, where the plane runs off to kilometres and the light is grazing anyway. And the cloud's
own detail noise (the sky's erosion and billow) is left out: at ground scale it would crawl under a moving car,
and one fetch at a mipmap level, which softens as the sheet's texels go sub-pixel, gives edges as soft as a
cloud 600 m up should.

## What it does to the light

Only the sun's direct light goes; the sky's fill stays, or a shade is a black hole. Both are carried by the
albedo, which the engine multiplies into the sun's diffuse lobe, into the ambient and into a metal's own
reflection tint, so the shaders cannot take the first without the second by multiplying there. So they scale the
albedo and add the fill back through `EMISSION`, which the engine adds to the pixel without the albedo:

```
float cloud = i76_cloud_shade(v_cloud_world);            // v_cloud_world: this shader's own world-position varying
EMISSION += i76_cloud_fill_back(cloud, METALLIC) * ALBEDO;   // the sky's fill, which the scale took
ALBEDO *= cloud;                                             // and the sun out with it
```

A `fragment()` is not given where it is — Godot has no world position among a spatial shader's fragment
built-ins — so each of these shaders passes its own `varying vec3` down from `vertex()`, which is also what
lets the plants hand down the swayed position and the horizon ring a position its `light()` could not ask for.

`i76_cloud_fill_back` is the sky's mean colour (`_builder.SkyAverage`, the sheet's own average) times the
environment's ambient energy, which is what the ambient of a sky-lit environment is; a metal is given none,
because the engine gives a metal no ambient at all (`ambient_light *= 1.0 - metallic`). Two things this buys: a
surface already in the sun's shadow map is not darkened twice — the direct term it loses is what the scale takes
and the fill puts back, so the two errors cancel under a bridge or a wing mirror; and a prelit face, whose
albedo is 0 and whose light is all `EMISSION` (the lamps, an illuminated sign), does not change, so a car's
headlamps do not go dim in a cloud's shade.

`horizon_terrain.gdshaderinc` lights itself in its own `light()` and would have its bounced term dimmed by an
albedo change, so it takes the shade where its sun visibility goes in instead, and its bounce fills against a
cloud exactly as it does against a slope.

The strength (`i76_cloud_shadow`, how much of the sun a thick cloud takes) and the fill are the only two numbers
the shaders are given; the policy is all in C#, because the look flips without a sky being rebuilt. `0` in the
1997 look, which has no sun for a cloud to step in front of — its own shadows are the projected faces of
`drop_shadow_1997.gdshader` (docs/deviations.md). By night the strength is 0.4 of the day's, and the fill comes
down with the environment's ambient energy by itself. `I76_CLOUD_SHADOW` and `I76_CLOUD_FILL` override them, and
Graphic Detail's Cloud Shadows row goes through `CloudShadow.SetEnabled`, which is another way of putting the
strength to zero (dev-hooks.md).

## How fast, and whether at all

Graphic Detail carries two rows for it. **Cloud Shadows** is On by default and takes the whole thing off — the
shade, the fill and the dome fetch — which is the same thing `I76_CLOUD_SHADOW=0` does and the row a player uses
to compare the two looks. **Cloud Speed** slows the sky and its shadows together to Half or Quarter, and it is
there because the sheet's pan is the original's own rate, which was chosen for a dome you watch from under: at
0.03 of the sheet a second the shadow crosses the ground at 45 m/s, which is a lot faster than a cloud 600 m up
and a 15 m/s wind would cast, and reads as weather moving at racing speed. Quarter is 11 m/s, about what a high
cloud really casts, and is the setting to try first on a heaped-sky day. Both rows apply where they are stood
on, are cancelled by Cancel, restored by Reset, and are held in shell.cfg for the next run (`I76_CLOUD_SPEED`
= `normal`, `half` or `quarter` overrides the row, as the other settings' variables do).

One honest coarseness: the pattern rides on the shader's own `TIME`, which C# cannot read, so changing the
speed moves the sheet to a new place rather than carrying the phase over — the clouds jump a little and continue
from there. What is kept is the relation that matters: the sky and its shadow read the same `i76_cloud_speed`
global, so they slide the same way and a shade never parts company with its cloud.

## Seeing it

Nothing here is in the simulation, a snapshot or a save; no test can see it, so it is checked on captures, as
the other shader work is. `I76_CLOUD_SHADOW=0` against the default is the A/B, and a day with a heaped sky is
the case to pick — the sheets differ a lot, and T01's is sparse. Two captures of one drive with the sun low,
one with the camera looking at the horizon and one down the road, show whether the shadows stand under their
clouds and whether a car driving through one darkens with the ground under it. For the drift, the same pair at
Normal and at Quarter (`--fixed-fps 60`, since the sheet rides on `TIME`) should show the clouds and their
shades moved the same way, not one of the two. If a shade reads grey, the fill
is too low; if a shaded ground reads milky or its edges glow, too high.
