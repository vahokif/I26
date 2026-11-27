# Modern look: haze over the distance, and the sun in it

The modern look had distance but not air. The ground faded into the sky's own colour by a distance the draw
distance chose, which stops a map edge being seen but does nothing else: every pixel between 100 m and the
horizon was as clear as the pixel at the bonnet, so a range of hills ten kilometres off had the same contrast
and the same colour as a sign at two hundred metres, and a drive into the evening light looked like a drive
under a glass plate. Real desert air is never like that. It is thickest low down, where the dust and the heat of
the plain keep it, and thin on the peaks; and it is brightest where it has the sun behind it. This adds both:
a kilometre of level air costs a little of what it looks like, a ridge two kilometres up costs almost nothing,
and the half of the horizon that is toward the sun goes warm gold while the half behind you stays the plain
colour of the sky.

It sits on the pass the modern look already had for its distance fog (`sky_fog.gdshader`), for the same reason
that pass exists instead of Godot's own fog: a fog of the engine's reads a blurred copy of the sky's radiance,
which smears the mission's horizon panels to grey and leaves a pale band where they meet the ground, so the port
fades into the sky's own function instead (see the header of `sky_fog.gdshader`). Haze wants the same thing —
it is the sky's colour seen in that direction, with the sun's share added to it — so it is the same four lines
of shader with an opacity that does not come from the clip. Nothing is sampled, no texture is read and no
altitude is looked up: the ray to each pixel is integrated analytically, and the depth buffer already says where
the ray stopped.

| Where | What |
|---|---|
| `game/Shaders/haze.gdshaderinc` | the maths: the air along a ray and the lobe over the sun, plus the six globals carrying the numbers |
| `game/Shaders/sky_fog.gdshader` | its host: the modern look's full-screen pass, which now lays haze as well as the clip's fade |
| `game/Scripts/World/DesertHaze.cs` | the policy: the numbers and their env overrides, the tint by the hour, the day and night shares, the map's own reference level, the look, the menu switch |
| `game/project.godot` | `[shader_globals]` entries for the six `i76_haze_*`: a `global uniform` is not registered by declaring it, and a push to an unregistered name is dropped silently (a test checks the shaders and the list against each other) |
| `game/Scripts/Boot/Main.cs` | `ApplySkySteps` starts it with the sky and measures the map's mean ground, `ApplyGraphics` takes the menu setting, `Rebase` moves the level, `SetMode1997Steps` turns it off, `ApplyDrawDistance` puts it on the screen |
| `game/Scripts/Shell/Overlays.cs`, `game/Scripts/Ui/EscapeMenu.cs` | Graphic Detail's Desert Haze row, and the same row on the pad inside a mission |
| `game/Scripts/Shell/Shell.cs` | the setting on `ShellSettings` and shell.cfg's `[port] desert_haze` behind it |
| `tests/I76.Game.Tests/DesertHazeTests.cs` | the numbers above, the lobe, the cap, and the globals being registered |

## How much air a ray sees

The density falls off with height over one reference level, which is the mission's own mean ground:

```
rho(y) = rho0 * exp(-(y - floor) / H)
```

and what matters along a ray is the integral of that from the eye to the surface the ray stopped on. With `d`
the metres of ray, `a` the eye's height over the level in scale heights and `x` the height the ray itself gains
over its length, in scale heights, the integral is exact:

```
tau = rho0 * d * exp(-a) * (1 - exp(-x)) / x          x = dir.y * d / H
```

which is the depth of the same length of level air at the eye's own height, times a factor that is 1 along a
level ray, under 1 for a ray that climbs away and over 1 for one that descends. That factor is the whole of
"stronger in valleys, thinner on peaks": a ray down a wash gathers the thick low air all the way along it, the
same ground seen from a ridge is seen through a ray that spent most of its length up in the thin air, and a
distant peak you look up at is nearly clear. `tau` is an optical depth and the pass lays `1 - exp(-tau)` of the
air's colour over the pixel, so it can never go milky-white over the whole map however far the ray runs.

Three guards, all of them for values a real mission produces. The fraction is 0/0 along a level ray, so below
x = 0.001 the shader takes the series (`1 - x/2 + x²/6`) instead — without it every pixel of flat ground at the
eye's own height is a NaN. A long downward ray makes `exp(-x)` overflow, which is fine (the cap below catches the
result) and the reason the cap is there: 8 is opaque and nothing beyond it is seen anyway. And the eye's own
height is trusted only down to 1.2 scale heights below the level, because the exponential has no ceiling and a
ray cast from under the map, or over the edge of it, would otherwise ask for an ocean of air: below that depth a
valley stops thickening at 3.3 times the plain.

None of this reads the terrain. The ground a ray stops on is already in the depth buffer, and its distance is
all the ray needs; the reference level is one number for the whole mission (`MissionWorld.AverageGroundHeight`,
measured once with the sky, as the horizon ring measures its own eye height once) and the eye's height above it
comes from the camera minus the origin `Rebase` moves. That is what makes it cheap — the pass already ran, and
this is a handful of transcendentals in it — and what makes it work unchanged on a huge map, on the distant
ring and past the map's edge, where the ground is not the streamed terrain at all.

## What the air is worth, and what the sun adds

The colour laid over a pixel is the sky's own function in that same direction, so the horizon where the haze
reaches 1 is exactly the horizon the sky draws, with no seam and no second palette — plus the sun's forward
scattering:

```
ALBEDO = modern_air(dir) + tint * (mie * phase(dot(dir, sun)))
phase  = a Henyey-Greenstein lobe over g = 0.6, scaled so that looking into the sun is 1
```

`modern_air` is `modern_sky` with its clouds and its sun or moon out of it (`sky_modern.gdshaderinc` draws both
from the same function; `modern_sky` asks for them, `modern_air` does not), because scattered light is the whole
column's forward share: it is smooth in direction and says nothing about the shape of a cloud tens of kilometres
up. The pass used the sky with its clouds left in, and a haze that lies 20 to 30% over a range of hills then
prints the cloud pattern onto the rock — which reads, exactly as it was reported, as a cloud seen *through* the
mountain. Taking them out costs nothing where the two are compared: the sky fades its own clouds out below about
seven degrees, and that is well above where any of these fades complete, so the ground still meets the horizon in
the colour the horizon is drawn with.

`g = 0.6` is what dust does: most of what it scatters goes forward, and the lobe is a fortieth of itself at right
angles to the sun, which is why the effect is a golden horizon on one half of the view and nothing on the other
rather than a global yellow wash. `tint` is not a new colour. It is the sky's own two colours for the sun's
light — `sky_modern.gdshaderinc`'s ochre low and near-white high, mixed by the same `smoothstep` of the sun's
height — so the lit air and the sun's disc hanging in it cannot disagree, and at night it is the cold blue the
sky uses for the moon with a fifth of the share: the moon is a fortieth of the sun, but the wash it leaves on a
distant range reads as a colour and not as a fortieth of one.

Two surfaces are hazed twice over, both harmlessly. The distant ring and the ground past the map
(`horizon_terrain.gdshaderinc`) light themselves and already fade themselves to the horizon colour with a crude
sun term of their own; since the haze is the sky's colour and the ring already sits at it, what that second pass
adds there is the gold and the height shaping, not a double fade. And at Original draw distance the clip's fade
reaches 1 by 560 m: the two are combined as `1 - (1-a1)*(1-a2)` — either way of going to sky, not one stacked on
the other — so the faithful-looking near view is unchanged by anything here.

## Where the numbers come from

`rho0 = 1e-4` a metre, `H = 400` m and `mie = 0.55` by day. They are chosen to be *subtle*, which is what the
brief asks and what a haze of this kind usually fails at: measured from the tests, a kilometre of level air is
worth 0.1 of optical depth, so 9.5% of that pixel is air, which is a car at 500 m untouched and a hill at 8 km
55% gone; one scale height up (400 m of climb, more than the maps' own relief) leaves a third of the air below,
and a wash a hundred metres down is worth 1.3 times the plain. The reach is deliberately much longer than the
draw distance's fade, which is 200 to 560 m at Original and 4.2 to 6.6 km on a huge map at Far.

That reach is the interesting part of the wiring. At Far on a normal-sized map nothing wanted the full-screen
pass drawn at all — there was no fade to show — so `ApplyDrawDistance` now keeps it drawn when the haze is on,
and pushes the clip's own fade out past the far plane in that case, so the only opacity the pixel gets is the
haze's. Original and huge maps behave exactly as they did.

| Number | Default | What it does |
|---|---|---|
| `rho0` (`I76_HAZE_DENSITY`) | 1e-4 a metre | the whole thing's strength; `0` turns the feature off |
| `H` (`I76_HAZE_SCALE`) | 400 m | how fast the air thins: the height that leaves a third of the plain's air below you |
| `mie` (`I76_HAZE_MIE`) | 0.55 (0.11 by night) | the sun's share; `0` leaves the haze the plain sky colour with no gold |

## Switches, and the 1997 look

**Desert Haze** in Graphic Detail (and on the escape menu's Graphic Detail pad) is On by default, applies where
it is stood on, is cancelled by Cancel and restored by Reset, and is kept in shell.cfg's `[port] desert_haze`
for the next run. The three numbers are env overrides for captures and tuning (docs/dev-hooks.md), of which
`I76_HAZE_DENSITY=0` is the same A/B as the row.

The 1997 look has none of it, and that is the faithful answer rather than an omission: the original drew the
world to its 600 clip and the sky's horizon panels beyond, with nothing between — no air, no gradient, the same
contrast at 100 m and at 600 (docs/deviations.md). F12 / `--1997` is the way to see the difference, and the
modern look's row is the way to compare it against itself.

## Seeing it

Nothing here is in the simulation, a snapshot or a save; no test can see the look, so it is checked on captures
as the other shader work is. The tests do pin the maths and the wiring that can be checked without a GPU: that
the numbers in the table above are the numbers the shader is given, that a ray along the layer's own level is
exactly density × distance (the series guard), that one scale height up is 1/e of the air, that a valley is
capped at 3.3 times the plain, that a long downward ray stays a number, that the lobe peaks at 1, and that every
global the shaders declare is registered in `project.godot` — an unregistered name drops its pushes silently and
the feature would simply never appear.

What only a capture shows is whether it is the right *amount*, and the pair to take is one drive at sunset with
the camera looking toward the sun and one looking away from it, `I76_HAZE_DENSITY=0` against the default
(`--1997` for the original's answer). Things to look for: a distant ridge should lose contrast and stay its own
hue, not go grey — grey means the Mie share is too low, or the tint's `air` mix too pale; the near car should be
unchanged, and if its bonnet goes warm the haze is reaching too close, so `I76_HAZE_SCALE` down or the density
down; the ground should not band as it climbs, which is what the clamp or a wrong reference level looks like (a
map where the haze suddenly halves at a hill's foot has its floor wrong: the floor is the mission's mean ground
minus the Godot origin, and `Rebase` re-pushes it); and looking up at a cliff against the sky, the seam between
the hazed ground and the unhazed sky should not be visible, because both are the same sky function.

The failure that was found this way is a cloud shape lying across a distant ridge, continuing over its silhouette
from the sky above: the airlight was the sky's own colour with its clouds in it (above), and a dark backlit rock
shows even the default's share of it. To see any airlight question clearly, take the pair at
`I76_HAZE_DENSITY=0.0008` — eight times the default, which turns a question about a few percent of a pixel into
a picture — and read the ridge, not the sky.
