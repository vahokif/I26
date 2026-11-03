# The map editor

An in-game editor in the spirit of Cities: Skylines: shape the ground with brushes, lay roads and place objects. Milestone 1 is
the editor mode, the terrain brushes, undo and redo, the playtest and saving; milestone 2 is the road tool (section Roads); milestone 3 is
buildings, props, plants, bridges and ramps, and the markers a melee, race or CTF map needs (section Building). It works in both looks;
the modern look is the one it is built for.

## Opening it

- Main menu: **MAP EDITOR** (bottom left, or **F8**). A list shows your maps (the ones in the install's `addon\` folder) above every
  stock mission. **Edit** opens the selected one; **New flat map...** asks for a name (1-8 letters, digits, `_`), a listed name (15
  characters), a size (1-4 tiles a side, 640 m each) and a height, writes the map to `addon\` and opens it.
- `I76_EDITOR=t01` (a stock mission, or the name of a map in `addon\`) starts the program in the editor (docs/dev-hooks.md).
- In the editor: **Esc** is the map menu (New flat map, Open, Save, Save as, Playtest, Exit).

## Keys

| Key | |
|---|---|
| Left button | paint with the brush |
| `1` `2` `3` `4` `5` `6` | raise, lower, smooth, flatten, level, roughen |
| SURFACE button (no key: `7`-`0` are the road tool's) | paint the ground's surface type (see Surface paint); `;` shows or hides its map, Alt + click or `I` picks the type under the cursor |
| `[` `]` (also Shift + wheel) | brush radius down, up (x1.15 a press; 5 to 400 m) |
| Shift+`[` Shift+`]` (also Ctrl + wheel) | brush strength down, up (2 to 100 %) |
| `G` | next falloff: smooth (cosine), linear, sharp, plateau |
| Alt + click, `I` | take the ground's height under the cursor for the Level brush (and select it) |
| `7` `8` `9` `0` | the road tool: straight, curve (`8` again: cubic), freeform, select and edit (see Roads) |
| `U` `X` (road tool) | upgrade (change a road's type), bulldoze |
| Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z | undo, redo, redo (a road and the ground cut for it are one step) |
| Ctrl+S | save |
| `W A S D`, arrows (Shift: 3x) | slide over the ground; the speed grows with the distance |
| `Q` `E` | turn |
| Right drag | orbit |
| Middle drag | drag the ground |
| Wheel, Page Up/Down | zoom (in, toward the point under the cursor) |
| `F` | bring the point under the cursor to the middle |
| Tab | pan with the mouse at the window's edge, on or off (off at first) |
| `B` | hide or show the brush circle |
| `F12` or `F2` | modern look or 1997 look, as F12 does in a mission; it works with a text field focused too, and in a playtest F12 is the mission's own (the 1997 look draws the horizon mountain panels by the original's rule, as a playtest, F5, does: their foot sinks 10.2 times the camera's height, so from the free camera, tens of metres up and more, they sink out of sight, as they do in the game on high ground). The editing view shows the ground past the map's tiles whole in both looks (see "The ground past the map" below) |
| `F5` | playtest, with the car under the cursor; `F5` or `Esc` comes back |
| `F1` | the help overlay |

**The ground past the map.** The editing view (not a playtest, which is a mission and draws as one) shows all of the ground past the
map's tiles (`OutsideGround`) in both looks, with nothing clipped: Draw Distance does not apply there (it is always Far: no 600 m clip,
no fade into the sky in the modern look), and the 1997 look, which in a mission draws the outside ground only within 600 m of the
camera and cuts it at 600 m view depth, draws it as far as the modern look does (`OutsideTerrain.EditorView`): the map's own 1997 terrain
within 600 m of the camera, the modern look's 10 m chunks lit as the 1997 terrain is (the engine's light per vertex, from the
gradient their normals carry, through the mission's light ramp, the ground texture; `outside_1997.gdshader`) out to 1.2 km, easing
onto the distant ring, which is drawn in the same light (`horizon_1997.gdshader`, its masks shared with the modern ring's in
`horizon_mask.gdshaderinc`) out to 20 km. An editor opened in the 1997 look makes the ring in the background (well under a second
on T01, the 1997 view showing the near chunks meanwhile); a mission in that look still makes it only when the modern look is first
asked for. In both looks the chunks' level of detail goes by their distance from the camera in 3D (a mission's by the distance
across the ground), so from high up the coarse ones are drawn. Frame times on an integrated GPU at 1280x720 (`I76_FRAME_PROFILE`,
GPU ms, T01 / canyonl3, the camera 150, 1500 and 3000 m up looking off the map): modern 5.9/6.8, 6.1/6.0, 6.0/6.3 (6 to 8
before); 1997 2.0/2.7, 3.0/2.8, 3.3/2.9 (1.6 to 2.7 before, when it drew little or nothing out there).

**Music.** The game's soundtrack plays while you edit: the CD tracks of the install the editor was opened from (Interstate '76's or the Nitro Pack's, whichever the map belongs to), a random one after another and never the one just played, at the Music level of Audio Control (nothing plays at level 0 or 1, or with no tracks in the install). The top bar's **MUSIC** button switches it off and on; the choice is kept with the player's settings (`editor_music` in the `[port]` section, written only when off). A playtest (F5) fades the music out for the mission's own and the editor starts a track again on the way back; leaving the editor ends it for the menu's music. `I76_EDITOR_MUSIC_SEEK=SECONDS` starts each track that far in (capture scripts: `music` presses the button, `key:F12` sends a key).
| `M` `P` | the object tools: select, build (the palette). `J` `R` `Y` `Z` arm a spawn point, regen pad, CTF flag, race checkpoint (the rest of their keys are in "Building" below) |
| `-` `=` | the path tool: edit paths, draw a new path (see Paths) |

The status bar shows the tool, radius, strength, falloff, the ground's height and position under the cursor, and the frame rate.

The tool column at the left is taller than most windows (it was cut off below the object tools, issue 146): it scrolls with the
mouse wheel over it or its scroll bar, as tall as its tools or the window allows, and a tool chosen by its key while its button is
out of sight is scrolled into view. The panels at the right (brush, road, object inspector, map check) and the keys overlay (F1)
scroll the same way when the window is short; the overlay's descriptions wrap to the window's width (`FitScroll`, `EditorStyle.Scrolling`).

## The brushes

Heights are the .ter's samples: 5 m apart, 12 bits in tenths of a metre (0 to 409.5 m), with the surface and edge nibble above them
left alone. A brush works on those samples (`I76.Editor.BrushStroke`): each frame the button is down it stamps along the cursor's path
(a stamp every 0.3 radius), each sample moving by `rate x strength x weight x dt`, where the weight is the falloff at its distance from
the centre (1 at the centre, 0 at the rim). A stroke keeps the heights as floats and writes them rounded, so a gentle brush adds up
instead of rounding away.

| Brush | Does | Rate (strength 1, weight 1) |
|---|---|---|
| Raise, Lower | add or take height | 24 m a second |
| Smooth | move toward the mean of the 3x3 samples about it | covers 12 x strength x weight of the gap a second |
| Flatten | pull toward the ground's height under the stroke's first stamp | likewise |
| Level | pull toward the typed (or picked) height | likewise |
| Roughen | add deterministic two-octave value noise (bumps of 20 and 40 m) | 12 m a second |

## Surface paint

The SURFACE button of the TERRAIN group paints the ground's **surface type**: the .ter sample's bits 13-15, the sim's `SurfaceId`
(bit 12 is the AI's impassable flag and is never touched, nor is the height). The type decides the tyre grip, rolling and
bump (WRLD's surface table), the tyre skid, turn and rolling sounds, the original's ground clutter and the modern
look's plants (docs/modern-vegetation.md: ids 2, 3, 4, 6 and 7 stay bare, 5 grows yuccas and cholla). It is not tied
to what the ground looks like: the ground texture is the map style's and the roads' own layers, so painting a type changes how a car handles
and sounds and which plants grow (they are redrawn), not the drawn ground. The right panel lists the eight classes:

| Id | Name in the editor | Rolling sound | What the stock maps use it for |
|---|---|---|---|
| 0 | DIRT | dirt | most of the melee arenas and A01 (small tufts) |
| 1 | SAND | sand | most of the trip and Nitro maps (bushes) |
| 2 | ASPHALT | none (road skids) | under asphalt roads (nibble 0x4) |
| 3 | DIRT ROAD | dirt | under dirt roads (0x6) |
| 4 | PLANKS | dirt | under plank roads and bridges (0x8) |
| 5 | GRAVEL | gravel | the rough ground (yuccas, cholla): S02, M06 |
| 6 | PARKING LOT | none (road skids) | under asphalt intersection pieces (0xc) |
| 7 | DIRT JUNCTION | dirt | under dirt intersection pieces (0xe) |

(The names are the editor's: the original names none of them; 6 and 7 are what its force feedback calls "parking lot" and "dirt
intersection".) The brush is a hard-edged disc of the chosen radius (surface is discrete, so strength and falloff are hidden); every sample
whose centre is inside it takes the type, a stroke is one undo step (`BrushStroke`'s surface mode, the same `StrokeCommand` as the height
brushes) and the status line says the type under the cursor. While the tool is on, the **surface map** (`SurfaceOverlay`) tints the
ground by type, one texel a sample over a 768 x 768 sample window that follows the camera and repaints the samples an edit reached; the
terrain shaders of both looks draw it (`i76_surface*` globals in `brush_cursor.gdshaderinc`). `;` or the SURFACE MAP button hides it. A road
the road tool draws, moves or bulldozes lays or resets its own nibble over its band again, so paint after the roads. Capture script:
`surface:ID[,overlay 0|1]` and `stroke:Surface,x,z,radius,1,seconds`. Tests: `SurfaceTests` (Editor: the disc, undo and redo, saving and
reading back), `EditorRoadSurfaceTests` (Sim: a car on painted gravel).

## Building

The OBJECTS group of the tool column (and the keys `M`, `P`, `J`, `R`, `Y`, `Z`; the road tool has `7`-`0`, `U`, `X`, `V`, `H`, `C`, `O`, `K`, `T`, and gives way to these tools and they to it) switches the brush for the object tools; `1`-`6` or a terrain button
switches back. The code is `src/I76.Editor/Objects` (no Godot: catalogue, layer, commands, footprints, snapping, road cut, map check) and
`game/Scripts/Editor/Objects` (`ObjectEditor`: input, ghost, selection; `ObjectUi`: palette, inspector, map check panel; `MarkerView`;
`Thumbnails`).

**The catalogue** is not in the repository: `ObjectCatalogue.Scan` reads every mission of the install (miss16 and addon: .msn, .cbt,
.rac, .cf2-4, .lvl), counts what each places by name and ODEF type, resolves the names to their .sdf/.vcf, measures the model's box, and
caches the result as JSON in `user://cache/` (about half a second to build; it is keyed by the archive and the stock mission files, so
saving a map of your own does not rebuild it). The ODEF type a name gets is the one the stock maps give it most often (a telegraph
pole is type 3, breakable, in the campaign and type 2, solid, in a few maps: the inspector offers every type seen). On the Nitro Pack it is 338 items (Buildings 61, Bridges and ramps 20, Roadside 109, Props 26, Plants 7, Fences 9, Markers 7, Scripted 99), on the base game 423 (70, 22, 115, 23, 8, 14, 7, 164); `i76 catalogue` prints them. Groups (`ObjectCatalogue.Classify`, from type, the stock naming and size):

| Group | What | Rule |
|---|---|---|
| Buildings | houses, shops, warehouses, barns, towers | names `b*` with a footprint of 30 m² or more |
| Bridges, ramps, tunnels | what carries a road: bridges (type 0xc), jump ramps, tunnels | `be*`, `aaramp*`, type 0xc |
| Roadside | road signs (type 4), poles, street lights, billboards, phone boxes | type 4, `atpole`, `bczlite`, `aobilbd`... |
| Props | wrecks, huts, water towers, pumps, planes | the rest of `a*` |
| Plants | cactus, pine | `n*` |
| Fences | fence and wall pieces | `afence*`, `bcaikwl*` |
| Markers | spawn, regen, checkpoint, the four flags | the loaders' keywords |
| Scripted | vehicles, gun emplacements, helicopters, gates | types 1, 7, 8, 9, `.vcf`: they need a mission script, so placed in a melee they are inert |

Placed scenery is written with the object flags the stock maps give it, 0 (the flag bits that do occur belong to vehicles: the player's car 0x10, the team and colour bits 0x10000-0x40000); the record's other bytes are zero, as in every stock file. Road pieces (the intersections, type 0x50-0x5f) are painted into the roads at load and are not placed by hand; the player's car is left
out. A name placed only by campaign missions is shown dimmer ("campaign only"): scenery needs no script, so it is harmless in a melee,
but only the "Scripted" group is marked as not working there. The palette is a bottom panel with a tab for each group, a search box
(names, across all groups), and a picture for each item, drawn once in an off-screen viewport and cached as PNGs in
`user://cache/editor-thumbs/` (the tab on show first; the modern look's materials). Hovering shows the description, the code name, size, ODEF type and how
often the stock maps place it.

**Labels.** An item's label is its description from the Complete Asset Bible (DIVER, Skull N. Bones, Brian Dean Jennings; "Building
No.100(large)", "Texas Rt. sign (South 137 Left)"), a name without one keeps its code in capitals. The descriptions are
`src/I76.Editor/Objects/asset-bible.tsv` (name, description, the bible's section, Gold/Nitro-only mark, and its note on a broken
object; only those, none of the bible's pictures or pages), embedded in the editor library and read by `AssetBible`. The code stays in the
tooltip and the line beside the buttons, and the search matches the description as well as the code. The palette's footer credits the
bible. The cache key is versioned (`ObjectCatalogue.CacheVersion`).

**Objects no stock map places.** The scan only sees what the maps place, so `ObjectCatalogue.Unplaced` adds the models in the data that no
stock map of either install places (found by `ObjectCatalogue.UnplacedNames` over both installs' stock maps, `MapFileSet.IsStockMission`;
the intersections, `i*`, are the intersection tool's), with the ODEF type of the bible's class (Struct1 solid 2, Struct2 breakable 3, SIGN
4, Ramp 0xb): the Industrial Park buildings `bcaik1/3/5/7`, `bcaik_s2/5/6/7`, the pillar `bcaikwl6`, `bfdepot1`, the cemetery
`bhyceme1-6`, `afence1-3`, shrubs and trees `ntree1-4`, `npinetr2`, `nadillo1`, props `abarrel1`, `actrash1`, `aitire1`, `a3pline1`,
the lit street light `bczlite2`, the overpass `aaramp6`, the tunnels `betunnl2/3` and 18 signs. They are marked "not in any stock map"
(dim, like the campaign-only ones). Each was saved into a map and loaded in the sim of both installs (`EditorUnplacedTests`: the model
assembles, the sim builds its collider, the world steps) and placed and drawn in the editor. Left out, with why
(`ObjectCatalogue.Excluded`): `aaramp8` and `agstlgt1` (the definitions assemble to no geometry; the bible says the ramp never appears and
the street light is part of a combined object), `swarn_1` (no geometry either), `axlot1/2` (parking lots that belong to the ground
texture) and `ngscrng1/2` (scrounge: named in the World Defs, not an object). The runway pieces `rerwend1-4`, `rmrwmid1` are placed by the
stock maps and are the intersection tool's; the bible's `ijd28t1` (crashes the game) and `akwreck1` are not scenery the scan reaches.
The base-game install's palette lacks what only the Nitro Pack's maps place (the bridges `bebridga`, `bewdbrg1`...: the data has them).

**Placing.** Pick an item (or press `J` `R` `Y` `Z` for a marker): a tinted copy follows the cursor, green where it may stand and red where it may not
(the status bar says why: it overlaps another object, the ground is too steep, it is on a road, it is off the map; Ctrl places anyway).
`Footprint` is the model's box on the ground, turned by its heading; overlap is the separating-axis test, a small overlap is allowed,
things smaller than 1.5 m² (signs, poles, cacti) never count, and the steepness is how far the ground strays from its height at the
origin over the footprint's corners, edge middles and centre (the loader stands the origin on the ground and leaves the rest to float
or sink): 2 m for buildings, 3 m for props and fences, 4 m for bridges and ramps; roadside things and plants stand anywhere.
Wheel, or `,` `.`, turns it 15 degrees (Shift: 1 degree); Ctrl + wheel zooms while placing. Click places, Shift + click keeps going, Esc stops.
`N` cycles the snapping: roads and grid, grid (5 m), off; holding Alt turns it off.

*Roads (Cities: Skylines style).* A building, prop, sign or fence within 55 m of a road goes to the side of the road the cursor is on,
its front (+z, the side the stock buildings face the road with: measured over every stock building, `i76 facing`) toward the road, set
back from the road's edge (6 m for buildings, 4 props, 1.5 roadside), sliding along the road with the cursor. Away from roads, the grid.

*Bridges, ramps, tunnels.* They carry a road, so they go by the road tool's own rule for structures (`StructureAttach.Ends` over the
sim's collider of the structure, `MissionSetup.StructureCollider`: the ends of its drive-on surface with position, height, outward
direction, width and slope). What the stock maps do (`i76 decks`, and `EditorObjectTests`: every stock bridge is a deck laid level with
the ground, its two ends are road ends, the stock roads end exactly on them; a jump ramp's *foot* is a road end, its raised lip is a drop):

- At an open road end within 22 m of the cursor, the structure's near end is put on the road's end and the structure goes on from it:
  nothing to cut.
- On the length of a road, the structure lies along it with its deck's middle on the road point, the way you have turned it (turning
  180 degrees flips a ramp's foot to the other side), at the road's heading; a bridge keeps to the axes (the engine's bridge search,
  `BridgeLinks`, ignores a slanted one), so on a road more than 25 degrees off an axis it is placed free instead.
- Placing it there also cuts the road (CUT ROADS, on by default; `RoadSplit`): the stretch that runs along the deck goes, and the road on
  either side ends at the deck's ends (a copy of the end point moved onto the deck end, the road's width and heading kept; a road that
  crosses at an angle, an overpass, is left alone). It is `RoadCommand` of the road tool's store (`RoadStore`: the roads are painted
  again by the road tool's painter, and written by Save), one undo step with the placement. A jump is two ramps with their lips toward each
  other: the road stretch left between the lips is the road tool's to bulldoze.
- A **tunnel** (`betunnl1-3`: a mountain with a tube through it; the tube is the model's TUN part, 12 m wide, 48, 108 and 156 m between its
  portals) is laid over the road the same way (its tube's middle on the road point, along the road, at any heading: it is not held to the axes
  like a bridge) but the road is **not cut**: the stock maps run the road straight through it (B01, P18; `i76 bridges betunnl1`), and the
  mountain stands on ground levelled under all of it (the stock terrain is flat there), so placing one levels the ground (as FLATTEN GROUND
  does, whether it is on or not) and it is not turned down for steep ground. It does not snap to a road's end (the road has to run through):
  the ghost says when the road stops short of a portal ("the road does not run through the whole tunnel"; the road tool draws on from there).
  `CatalogueItem.IsTunnel` / `CutsRoad`, `DeckCover.Gap`. A car drives its tube from portal to portal in the sim
  (`ACarDrivesThroughATunnelFromPortalToPortal`).
- The road tool's own **bridge and tunnel modes** are the way to lay one over ground that is not a road yet: see Bridges and tunnels in the road
  tool's section below. (The build tool, as here, puts one structure onto a road that is already there.) The base-game palette has the Nitro
  Pack's bridges, ramps and the short tunnel too (`ObjectCatalogue.NitroPackStructures`: the model files are the same; "Nitro Pack maps only").
- The ghost says when the deck's ends stand above or below the ground at the road (the game stands the structure's origin on the ground,
  so a slope leaves a step): `L` levels the ground under it.

*Levelling.* FLATTEN GROUND (`L`) levels the ground under a building, prop or structure as it is placed: the brush's Level at the mean
height of the footprint's corners and centre, a flat core and a soft skirt (14 m), as one stroke that is part of the same undo step.

*Where it stands.* A placed object is written with its y at the ground's height there, and the game stands every object on the ground
at load anyway (`MissionWorld.GroundedPosition`, docs/map-format-writing.md); the editor draws it the same way and keeps it on the
ground when the ground is edited.

**Selecting.** `M`: click selects, Shift + click adds or removes, dragging on empty ground draws a box, Ctrl+A selects everything.
Dragging a selected object moves it over the ground (grid and road snapping as when placing, for one object; the grid for several);
Alt + drag leaves the selection and drags copies; `,` `.` turn the selection about its middle; Del, Backspace or `X` bulldoze;
Ctrl+C and Ctrl+V copy and paste (the copy follows the cursor like a ghost, a click places it). A small inspector at the right shows
the name, the ODEF type (every type the stock maps give the name), the heading and position, the team of a spawn or flag, and a
checkpoint's number with EARLIER / LATER buttons (they swap numbers with the neighbour) and "make the finish line".
Everything is undoable (Ctrl+Z / Ctrl+Y): placing (with the levelling and the road cut), moving, turning, copying, bulldozing,
editing a record, re-ordering checkpoints. One command per step: `ObjectCommands` (Place, Remove, Transform, Edit, EditMany, Group, with
the road tool's `RoadCommand` for a cut) over the `ObjectLayer`, which holds the ODEF records in file order, reports each change to the drawing
(`ScenerySpawner.EditShow`, `EditHide`, `EditMove`) and writes them back through `MapDocument.Objects` on Save.

**Markers.** `J` spawn point (numbered in file order; in a CTF map for a team with `T`, 1 to the map's team count: `#spawnN`), `R` regen pad, `Y` flag (team with
`T`, 1-4: `a1flagN`), `Z` race checkpoint (the next free number, `check1`..`check98`; `T` makes the next one the finish line,
`check99`). Spawns are drawn as a car-sized pad with an arrow and its number, regen pads as an orange pad, flags with a ring in the team's
colour, checkpoints as a pole with a banner, the number, and the 30 m ring the race code tests; a gold line with an arrowhead on each leg
shows the lap direction (1, 2, 3 ... the finish, and a dim leg back to 1).

**The map check** (the panel at the right, always in the object tools; a summary when you save) follows what the loaders need
The map's mode is its extension: `.cbt` melee, `.rac` race, `.cf2`/`.cf3`/`.cf4` CTF with 2 to 4 teams.

| Mode | Error | Warning |
|---|---|---|
| any | no spawn point; more than 64 spawns or regen pads | |
| melee | | only one spawn |
| race | no checkpoints; a gap in the numbers (check1, check3: 2 is missing) | fewer than two; a number placed twice; two checkpoints closer than 30 m (their spheres overlap); two finish lines. Info: no finish line (laps count at checkpoint 1) |
| CTF | a team with no flag, or with no spawn (a plain `spawn` counts for team 1: the engine puts it in the first table) | two flags for a team; a flag or spawn for a team the map does not have |
| other | | flags in a map that is not CTF, checkpoints in one that is not a race (Info) |

**Playtest.** F5 builds the world from the edited objects (`EditorSession.Prepare` replaces the world's placements with the layer's),
so what you placed is there, with collision: a car driven at a placed bank stops against it (from 20 m/s to rest at the wall, where on the
same map without it the car runs on through), and bulldozed props are gone. The layer and its undo history cross the reload like the heights.

## What is redrawn

`IMapDocument` / `TerrainDocument` wrap the mission's own `TerrainTiles` (the object the game draws and the sim walks), so an edit is
seen by everything built from the world. After an edit, `MapEditor` redraws what it reached:

- **Terrain** (`TerrainNode.RefreshRegion`, every 60 ms while painting): `TerrainSurface.Refresh` brings the modern surface's arrays
  (the needle filter, the blur, the open cells, the lean-back cone, the rims) up to date in place for the edit and what it reaches
  (8 samples about it). The chunks it touches draw as they were until their new meshes are made, a few at a time on the thread pool
  (`TerrainLodMesh.Build` at the level on show), then swap in; the other levels are dropped and made when the camera wants them. The
  1997 meshes of stale chunks are rebuilt while that look shows. Reachability is not re-run: inside an edit a gentle cell is
  drawn as drivable ground, a cliff as a cliff.
- **Objects** (`ScenerySpawner.Reground`, at most every 0.4 s and at the end of a stroke): every placement in the rectangle is stood on
  the new ground (`MissionWorld.GroundedPosition`), as the loader does. Gates and spinners keep their placed height.
- **Plants** (`VegetationNode.Invalidate`): the chunks the edit touches are dropped and placed again on the new ground.
- **Cavity maps** (`TerrainNode.RefreshCavities`): the hollows the ground materials put gravel in.
- **Roads** are painted into the terrain and follow it. When the road tool changes them, only the chunks they reach are painted again (see Roads).

Not redrawn: the distant horizon ring (`HorizonTerrain`: it is made from the map once, so an edit at the map's very edge is not in it)
and the ground past the map's tiles (`OutsideGround`, `OutsideTerrain`: made from the map's edge when it loads, so it follows an edit there
from the next playtest or reload, which the simulation drives on too),
the grass tint map (`SwardMap`, the whole map, once), the ground faces painted from scenery (`GroundDecals`), rolling tumbleweeds'
cached points. A reload (a playtest, opening a map) makes all of them again.

Cost, at 1920x1080 on an integrated GPU (`I76_EDITOR_SCRIPT` strokes on T01, the main thread's frame time while painting): a 60 m brush
about 11 ms a frame (88 fps), the largest brush (400 m) about 22 ms (45 fps), the worst frame about 50 ms.

## Undo and redo

Roads, objects, paths and the map style have commands of their own on the same stack. A stroke is one command (`StrokeCommand`): for each tile it touched, the indices of the samples it changed with their raw values
before and after. `UndoStack` keeps them (6 million samples at most; the oldest go first). A new stroke drops what was undone.
A tile the ZMAP grid shows in more than one cell is one set of samples, so an edit there shows in every cell, and is stored once.

## Roads

The road tool (`game/Scripts/Editor/Roads`, the maths in `src/I76.Editor/Roads`) draws roads in the original format. `7` `8` `9` `0` `U` `X`
or the palette's ROADS group turn it on; a brush key (`1`-`6`) turns it off. The panel at the right replaces the brush's: the type of new
roads (ASPHALT, DIRT, PLANKS: the three texture families of RSEG kind % 3), the snaps, chaining, whether the ground is cut and filled, the
maximum grade and the shoulder.

**What a road is in the file.** One RSEG per road: a polyline of points with the left and right edge of the road at each, nothing joins two
roads, and the AI routes along one road only. The tool writes what the 1501 stock roads of both installs
have: 10 units between the edges, the edges square to the centre line (to the central difference of the neighbouring centres), the left
edge on the (-tz, tx) side for the direction of increasing index, points 5 apart (5 to 5.5: a curve is cut into equal steps), the point's
left y word the mean z of its edges, the right y word the stock's 0 then 36, a closed loop ending on its first point again, a road of at
least 3 points (the engine builds nothing from two). A road's y is not in the file: the engine puts every road point on the ground's
height at load, so a road follows the ground, which is why the tool shapes the ground (below). The survey and the conventions are in
docs/map-format-writing.md.

**Drawing.** Click to start, click to end (`Esc` cancels, `Backspace` takes the last click back, `Enter` or a double click finishes).
- Straight: start, end. Hold Shift on a click (or turn Chain on with `C`) and the next piece goes on from it: one road, its corners
  rounded to a radius of about 32 (stock roads turn under 9 degrees a point in 99 % of cases).
- Curve (`8`): start, control point, end, a quadratic Bezier; `8` again for a cubic (start, two controls, end). Shift chains.
- Freeform (`9`): click points, a spline passes through them.
- The preview is the real road mesh and texture (`RoadNode`, both looks) with a ribbon over it for the grade: green up to 60 % of the
  limit, yellow up to the limit, red beyond, for the ground's own steepness along the path. The panel says the length, the points, the
  steepest ground, the cut and fill the grading will need; a bend tighter than 6 units is refused (the quads would fold).

**Snapping** (rings show what the cursor has found; strongest first):
- the end of a bridge deck, the foot of a jump or ramp (cyan): the point lands on the end edge's middle, the road meets it square and at
  its height. Stock maps do exactly this: a bridge's two ends are the ends of two roads, and a jump ramp's foot is a road end (T05, M05,
  the arenas). The raised lip of a ramp is not snapped to.
- the end of an existing road (green): the new road extends that road (its own points are kept byte for byte; the new points continue its
  heading and keep its type). Drawing toward a road's end does the same, backwards. The other end of the same road closes a loop.
- the middle of a road (orange): the road ends there and an intersection piece goes down (section Intersections). Where no piece fits
  (an angle that is neither square nor about 41 degrees, planks for a T, less than 12 m of road) the hint stays: the game has no junctions,
  the road stays separate and AI cars will not turn there. A road that crosses another gets the same hint where no X fits.
- the free arm of an intersection piece (cyan, like a bridge end): the road meets it square on, at its height. Arms with no road at them
  come from a piece whose road was bulldozed or never drawn.
- 15 degrees from the previous piece's heading (the world's z axis for the first), `V` toggles; a 10 m grid, `H` toggles (with both, the
  length rounds to the grid). Angle snap is for straight roads. Each snap can also be turned off in the panel.
- Structure ends are read from the map's own placements (`MissionSetup.StructureCollider`, the sim's drive-on surfaces; the ends from
  `I76.Formats.Assembly.StructureAttach`), a few a frame after the tool opens.

**Intersections** (`src/I76.Editor/Intersections`, `game/Scripts/Editor/Intersections`; the stock layout, the rules' numbers and the
survey are in docs/map-format-writing.md, "Intersections"). The game's junctions are hand-laid: roads that stop at a gap, and one of the
`i*isect*` or `ita21_1` road-piece objects standing in it. The road tool does that as the road is made, like Cities: Skylines:
- **When.** A new or edited road that ends on the middle of another road (within 1.5 m of its centre line), or crosses one, gets a piece:
  within 15 degrees of square, a **T** (end on a road) or an **X** (crossing); within 10 degrees of 41, leaning forward along the through road,
  a **fork** to the right or left; any other angle, no piece. The piece is of the road's kind (asphalt in the map's most common look, dirt
  or planks; planks have forks only), at the through road's ground height, rotated to it: its arms land on the roads.
- **The roads are cut** at the arms as the stock maps' are: the through road becomes two roads, each ending on the middle of an arm's edge
  and running straight along it for its last 5 m (the road is bent into the arm over the last 7 m), the stem ends on its arm (a T's, 5 m
  from the through road's centre line; a fork's branch 23 m from the junction point). A road with too little left to stand on (under 7 m
  past the arm, plus the lead-in) gets no piece. The ground under the piece is levelled (with `O` on) and takes the stock intersection surface
  (nibble 0xc asphalt, 0xe dirt: grip rows 6 and 7 of the world's table), all in the road's own undo step (`JunctionCommand` in a `StepCommand` with the
  road's `RoadCommand`: roads, pieces and ground go and come back together).
- **The preview** shows the piece (tinted, over the ground) and says what it will be while the cursor is on the road it will end on.
- **Edits follow.** Moving the end of a road off its arm, bulldozing it (or the through road), cutting a point out, retyping: a piece that lost
  a road at an arm changes or goes. An X that lost one becomes a T of the other three arms; a piece that lost its through road or its
  stem goes and the two roads that ran through it are one road again (a straight run over the gap); retyping the through road changes the
  piece to its kind (planks have no T or X, so it goes). A new road made or moved onto a road gets its junction as a drawn road does. Bridge ends use `RoadSplit`
  (objects); the pieces cut with `Junctions.Part`, which also straightens the lead-in the stock roads have (the cut of `RoadSplit` keeps the road's
  own heading at the cap), and both go through the same `RoadStore` and `RoadCommand`.
- **AI.** Nothing changes: the pieces are painted squares and a different grip, the AI still routes one road at a time, and the cut through road is two
  roads (as in the stock maps). The stock game does nothing at its pieces (docs/map-format-writing.md); the hint says so.
- **Saving.** The pieces are the map's road-piece objects (type 0x50, 0x52, 0x53): `PieceStore` is what Save puts back over the object
  layer's copies, in the same places in the file for the map's own pieces (new ones last: object order is the file's, scripts may count on it).
  An edit script has `road:pieces` to list them.

**Surface.** The ground's surface nibble under a road is set as the stock maps' is (0x4 asphalt, 0x6 dirt, 0x8 planks, within 10 m of the centre line and 2.5 m past the ends; `RoadSurface`, `i76 roadsurf` for the survey, docs/map-format-writing.md "The ground under a road"), so tyre grip, rolling, bump and the tyre sounds are the road's: a car reads it through the sim's `SurfaceId`. The nibble goes in the road's own undo step (the `HeightRecorder` of the road's `RoadCommand`) for drawing, extending, dragging points, retyping and bulldozing; the ground a road leaves (moved, retyped, bulldozed) takes the commonest nibble beside it, a crossing road keeps its own band, and the ground under an intersection piece (0xc, 0xe) is never overwritten. `O` (conform) governs the heights only. Tests: `RoadSurfaceTests` (Editor; the band against 1500 stock roads), `EditorRoadSurfaceTests` (Sim).

**Grade and ground.** The profile starts from the ground along the centre line, is smoothed (sigma 15 m) and held to the maximum grade
(default 10 %: the median of the stock roads' own steepest 5 % of steps, 0.095; settable 2 to 40 %), with the heights of what the road
meets pinned (a deck, the ground an extended road already stands on). With `O` on (the default) the ground is cut and filled to that
profile: the samples within 8 m of the centre line take the profile's height (so the quads, which the engine puts on the bilinear ground,
have no cross slope and no bump: the road's edges land within 0.35 m of the profile in the tests), and a shoulder (default 14 m, 0 to 40)
blends back to the old ground with a smoothstep. With it off, the road is laid on the ground as it is. The road and its ground are one
undo step (`RoadCommand`: the road list change and the height deltas). Moving a road or bulldozing it leaves the ground as it was graded.

**Bridges and tunnels** (`RoadStructures`, `RoadToolStructures.cs`). The road tool's panel has BUILD ON: GROUND (the road follows the terrain, as
above), BRIDGE and TUNNEL (Shift+Tab cycles them; the BRIDGE and TUNNEL buttons under the road tools turn the mode on from the palette). With
BRIDGE or TUNNEL on, STRAIGHT, CURVE and FREEFORM alike lay one **straight stretch**: click where it starts (it snaps like a road's start: a road
end, a deck end, and the angle snap), move toward the far end (the cursor only says which way and how far), click again (or Enter). **Curves are
not laid**: a stretch is a chain of fixed straight pieces, 60 m and longer, and a joint can turn only about 6 degrees before it opens half a metre of gap at the
deck's edge, so a curve would need a radius of over half a kilometre; the status line says so and a curved drawing becomes a straight one. Mixed drawing
works as drawn: draw a ground road, switch (the road so far is made, and the stretch starts at its end), lay the span, switch back and
snap to the span's end (it is a structure end to the road tool: its heading and height are kept; the tool keeps reading the object layer's structure
ends, so what it just laid is there). Chain (`C`, Shift) goes on from the stretch's end the same way.

*Why the pieces are laid as they are.* Two facts about the game decide it. The game stands every placed object on the ground at its origin at load
(`MissionWorld.GroundedPosition`), so a piece's height is the ground at its origin; and the stock bridges are straight, 10 m wide, with no piers
(`bebridg1-4`, `bebridga/b` 60, 80, 100, 160 m; `bewdbrg1/2` 80, 60). Their collision deck is a 1 m hump (it rises over its first 20 m and falls
over its last 20: `i76 bridges`, the sim's `StructureCollider`), level at the ends. So:

- **Pieces and snapping.** The span is cut into whole pieces and the stretch ends where they do, never stretched or scaled; the preview shows the
  pieces as tinted ghosts, the grade as a ribbon over the deck and, where the ground is raised, a post at each joint. AUTO tries each style (low concrete
  decks, timber, tall trusses: told apart by their height, never mixed in one bridge) and takes the one that lands nearest the drawn length, a joint
  costing 6 m of error (a joint is a 1 m dip over 40 m, the two humps meeting: `StretchPieces.JointCost`); the PIECE chooser lays one model, repeated
  instead. The stock overpass `beovrps1` (30 m wide) and `bcaik8` (a building the maps give the bridge type) are not offered. A stretch shorter than
  half the shortest piece is refused.
- **Grade.** The deck runs on one straight grade from the start's height (the ground there, or the road or deck end it starts on) to the ground at the
  far end, held to 7 % (below `StructureAttach.FlatSlope`, so the road tool still snaps a road to the end of a pitched deck as to a level one). Each piece is
  pitched about its own right axis (the record's rotation matrix: `RoadStructures.Rotation`; its heading, `MapObject.Yaw`, is unchanged) so that
  the decks join end to end at the height the ground has at each origin.
- **Piers.** Each piece's origin stands on the ground, so under every joint whose ground is not already the deck's height the ground is raised (or cut)
  to it: a small flat top along the deck's start edge with a short slope (the four samples round the origin are forced to the height, so the
  bilinear ground the game stands the origin on is it to 0.1 m). On a valley that is a pillar of ground under each joint, the rest of the terrain
  untouched. The first piece and the far end get a wider landing (the road's shoulder) so that the roads meet the deck at its height. Where the ground
  already is the deck's height nothing is written.
- **Roads.** A road under the bridge that runs along it is cut at the deck's ends (`RoadSplit`, as the build tool does; CUT ROADS in the object
  tool's settings), crossing roads and rivers are left alone. Terrain edits, the cut and the pieces are one undo step (`ObjectCommands.Group`).
- **Warnings** (amber): the deck is buried (the ground stands above it by 0.4 m or more for some length, away from the joints, whose ground is
  cut to it), the grade is held (the far end stands off the ground, which is graded to meet it), the bridge is more than 25 degrees off the map's axes
  (the AI's bridge search ignores it: `BridgeLinks`; a car still drives it). Note: how many joints get a pier of ground.
- **Tunnel.** One piece, the tube nearest to the drawn length (betunnl1 48 m, betunnl2 108, betunnl3 156 between the portals; the Nitro Pack's
  base-game palette lists them all). A chain would put a portal in the middle of the hill, so a longer drawing lays the longest tunnel and the next
  stretch goes on from its end. It is **level** (a mountain model is one flat slab): at the height of the road end it starts from, else the mean of
  the ground under its footprint, and it levels the ground under the mountain to it as the build tool does. The road runs through the tube level at that
  height (a road of the panel's type, extending the road it starts at, pinned at every point), with the junctions a road makes. Warnings: the ground is
  more than 6 m below the floor under the mountain (it stands on fill and sticks out of the terrain), the tunnel is cut more than 25 m into the
  hill, a portal stands more than 4 m from the natural ground.
- **Limits**: no curves; no graded tunnel; no ground raised over a tunnel. Pieces keep their pitch when moved with the object tool but turning one
  replaces its rotation by a pure heading, and since the game stands every object on the ground at its origin, sculpting the ground under a piece's
  origin moves its deck (undo, or lay the stretch again).

Tests: `RoadStructureTests` (Editor: the pieces and their choice, the grade and its clamp, the warnings, the pitch matrix, that the decks join and the origins stand
on the ground, one undo step), `EditorRoadStructureTests` (Sim: a ground road, a bridge over a valley on a slope and a ground road drive end to end in a
saved map without sinking through the deck or a jump at a joint; a tunnel with its road on a slope drives portal to portal).

**Editing** (`0`, select): click a road to select it; its points show as handles.
- Drag a handle: the road bends about it (the 6 points either side follow by a smoothstep; Alt: that point alone). The ground under the
  moved stretch is graded when you let go. An end dragged onto a bridge or ramp snaps to it.
- Double click on the road or `Insert`: a point at the cursor. `Delete`: the selected point, or with none the whole road. `K`: split the
  road at the selected point into two roads that share it. `T`: change the type of the selected road (also the panel's buttons).
- `U` (upgrade): click a road to give it the panel's type. `X` (bulldoze): click a road to remove it. Ctrl+Z undoes any of it.

**What is redrawn.** The road layers of the terrain chunks the edit reaches are rasterised again (`RoadPainter`: the loader's own
`RoadRaster`, the gap fills, the piece tops, on the thread pool; 4 to 8 chunks take 50 to 130 ms) and swapped into those chunks' materials
(`TerrainNode.SetRoadLayer`); the ground redraws as in the brushes. The edited roads are also given to the world the game builds
(`MissionFile.RoadOverride`), so a playtest (F5), the AI's road routes and the 1997 look have them. Not redone: the plants and ground
decals near a new road, the reachability the terrain surface computes at load.

**Playtest and the AI.** F5 drops the car under the cursor; point the camera at the new road to drive it. The sim reads the new road
as it does a stock one: `EditorRoadTests` makes a road with the tool, saves it, and sends an AI car along it (start and goal within 10.5 of
the road): the route has one waypoint per road point, 2.45 inside the edge, and the car drives it to the end staying within 3 of the centre
line (all 458 ticks on the road).

## Paths

The **path tool** (`-` and `=`, or the PATHS group at the bottom of the tool column; `game/Scripts/Editor/Paths`, the table and
its edits in `src/I76.Editor/Paths`) edits the **nav paths** of the map's mission script: the routes and spots its orders and
tests name. `goto`, `race` and `evade` send a car along one, `teleport` puts a car at its first point, `isWithinNav` and
`isWithinSqNav` test a car against its first point (a circle, a square), and the cut-scene cameras stand on or travel along one.
They are the original mission builder's path layer (Arsenal, shipped for I'76 Gold and the Nitro Pack: a Path Layer toggle and a
Path Toolbox with four tool buttons, new path, move point, delete path and insert point, and a "Path Label" field).

**What a path is.** A label (up to 39 characters) and points in the ground's plane, in the order a car drives them. They live in the
script's payload (`ADEF`/`FSM`, docs/map-format-writing.md, "Nav paths"); every stock point is at height 0, the game takes the
ground's height wherever it needs one (a teleport, a camera) and the AI drives them in x and z, so the editor draws a path on the
ground and writes its points at height 0 as the stock files have them. The script names a path by its place in the table, not its
label: the original's script compiler wrote one copy of a path for every place that names it (`bridgenav` four times in P18,
`panpath` three in T01). The editor shows the copies as one path and every edit changes them all.

**The layer.** Each path is a line draped on the ground with an arrow on each leg (the way it is driven), a ball at each point (the
first one white and larger) and its label by the first point; the selected path is amber and its points are handles (the selected
one orange, the one under the cursor yellow). It is drawn over everything and unshaded, the same in both looks. It shows while the
path tool is on, and with every tool when SHOW PATHS (the palette, or the panel's SHOW PATHS WITH EVERY TOOL) is on.

**The tools** (the original's four buttons):

| Mode | Key | Does |
|---|---|---|
| EDIT PATHS | `-` | click a path (its line or a point) to select it; drag a point to move it; a double click on the selected path's line, or `Insert` with the cursor near it, puts a point there; `Delete` or `Backspace` removes the selected point, or with no point selected asks to delete the path; `Esc` deselects |
| NEW PATH | `=` | click the points on the ground in driving order; `Enter`, a right click or a double click finishes (a right click otherwise orbits as ever); `Backspace` takes the last point back, `Esc` cancels. The path is labelled `path1`, `path2`... and selected for its label |
| INSERT POINT | | click on a path's line: a point goes there, and holding the button drags it into place |
| DELETE | | click a point to remove it (a path keeps at least one: the tests read its first); click a line to delete its path, after "Delete the path NAME?" (YES / CANCEL) |

Picking takes the nearest point within a few metres (more as the camera pulls back), else the nearest line.

**The panel** (at the right, in place of the brush's): PATH LABEL (Enter or leaving the field renames the selected path; a label
another path has, an empty one or one with characters the file cannot hold is refused, with the reason in red), the point count
and length, how many copies the table holds, where the script names it (`goto (3)`, `isWithinSqNav (1)`...), DELETE POINT and
DELETE PATH, and the list of the map's paths (a click selects one and brings the camera to its first point; `*` marks one the
script names).

**What the script names** is read from its bytecode (`FsmPathUse`, I76.Formats): an order's or test's path argument traced to an
immediate or a thread parameter (a data cell), through calls. A script that keeps a path in a local variable and sets it as it runs
is not followed, so a stock path the analysis finds nowhere ("Not found in the script's orders") may still be used: about three
quarters of the stock maps' path entries are found.

**What cannot be done.** I26 has no script compiler: the editor edits the compiled script's table, which the script indexes. So a
path the map's script came with can be moved, reshaped and renamed but not deleted (it would move every later entry; DELETE PATH
says why), and a new path goes at the end of the table and is saved with the map, where no compiled script names it. On a map
with no script (every melee, race and CTF map) any path can be deleted. docs/deviations.md has the difference from the original
mission builder, which compiled the script from its own workspace (its paths by label).

**Undo, playtest, saving.** Every edit is one undo step (`PathCommand` over the `PathStore`, the table in file order; `Ctrl+Z`,
`Ctrl+Y`), and the store and its history cross a playtest like the roads. The world a playtest builds reads the edited table
(`MissionFile.FsmOverride`, set by `EditorSession.Prepare`: "editor: the world reads the edited paths" in the log), so a script
that runs there, the plants the modern look keeps off the paths and the drivable-ground seeds all see the new points; the playtest
itself runs the map as a melee, without its script. Run as a mission, the script's orders follow the edited points:
`EditorPathTests` (tests/I76.Sim.Tests) moves every path of S04 a metre, builds the mission from the override and checks that the
paths its intro drives the cars along are the edited ones. Save writes the table into the script's payload when it changed (the
rest of the script byte for byte); a map with no script that gets paths is given empty script tables around them, which the game
reads as no script. Saved as a copy, a story mission becomes a melee map (`.lvl`, `.cbt`) as before, so its script does not run in
the copy; the paths are in the file.

## Geographic maps

`i76 geomap --bbox LAT1,LON1,LAT2,LON2 --name NAME --out DIR [--roads major|all] [--game i76|nitro|both] [--stem STEM] [--hour H]` builds a
playable melee map from real terrain and roads and writes the map files and a README.txt (the area, the sources, the licences, what was left out)
into DIR: copy them into the game's `addon\` folder. The core is `src/I76.Editor/Geo` (no Godot, no network but `GeoFetch`), the command is
`tools/I76.Cli/GeoMapCommand.cs`; `--install` (or `I76_INSTALL`) lets the look be checked against the archive. The map is a `.lvl` (`i76`), a
`.cbt` (`nitro`) or both, with one `.ter`; `--name` is the listed name (15 characters), the file stem its letters and digits (8; `--stem`).

- **Downloads**, cached under `~/.cache/i26-geo/` (never in the repository, fetched once): the AWS terrain tiles at zoom 14 (`terrarium` PNGs,
  about 6.7 m a pixel at 46 degrees, 0.1 MB each, 49 tiles = 4.7 MB for a 10 km box; heights from the USGS 3DEP in the US) and one Overpass
  query for the box's `highway` ways (about 0.45 MB; it tries three servers and waits when they are busy). Attribution (also in the README):
  Mapzen/AWS Terrain Tiles (USGS 3DEP), (c) OpenStreetMap contributors (ODbL).
- **The grid.** The box is centred on whole 640 m tiles (`GeoGrid`): the tile counts round up, except that a box up to 5 % of a tile over a
  whole number is cropped to it, and a box needing more than 254 tiles (tile ids are bytes, 255 is empty) or more than the 80 x 80 cells is cropped
  to the largest centred block that fits, which the README says. Map x is east, z north, one unit a metre, the last row of tiles in grid row 78 and the first
  column 4, where the stock arenas sit. The projection is local equirectangular about the box's centre (`GeoProjection`: the WGS84 metres a degree of
  latitude and longitude span there; a few metres off at the edges of a 10 km box).
- **Heights.** The terrain is sampled bicubically (Catmull-Rom over the 4 x 4 pixels, `TerrariumMosaic`) at every 5 m sample. The lowest ground
  less 10 m is sample 0, and the samples are tenths of a metre, so the format's 409.5 m holds 399 m of relief with the 15 m margin above; more is
  multiplied by one factor to fit (`PackedHeights`, "compressed" in the README).
- **Roads** (`OsmRoads.Classify`, `RoadImport`, `GeoMapBuilder.LayRoads`). `major`: motorway, trunk, primary, secondary and tertiary with their links
  as ASPHALT, unclassified and track as DIRT (as is any way OSM tags with an unpaved surface); `all` adds residential, living street, service (not
  driveways, parking aisles) and `road`. Footways, paths, cycleways and the like are never imported. A way is cut to the map (12 m in), simplified
  (1 m), its corners rounded (radius 30, never more of a leg than the corner's share), cut into steps of about 5 m and split where a bend is still
  tighter than 6 m (the quads would fold). The roads are laid biggest first: each takes the road tool's grade profile (10 %, smoothed), the ground is cut and
  filled to it (8 m flat, 14 m shoulder: `RoadTerrain.Conform`), a road meeting ground a bigger road already levelled follows its height there
  (pinned), and the ground under the roads gets the stock surface nibble (`RoadSurface`, smallest roads first so a main road's wins). A `bridge` way
  is not cut into the valley: its deck is a straight, grade-limited line between its ends and the ground is only filled up to it with a wide skirt
  (the engine puts a road on the ground, so a bridge without a bridge object is earth); a `tunnel` way is left on the surface. No bridge objects and no intersection pieces
  are laid (they are the road tool's, by hand): junctions are roads that overlap. Bridge ways and a road crossing a levelled one meet at one height.
- **Limits.** Neither the RSEG reader, the sim's road network, the AI's route code nor the editor holds a road or point cap (the engine
  keeps the pieces in one linked list in load order), the terrain's 254 tiles are the one hard limit. Measured on the
  Billings map, major roads (75 roads, 17 000 points) against everything (380 roads, 40 000 points, 200 km): the game's mission build 10.4 s
  against 10.9 s (nearly all of it the 240 tiles), the frame 9.4 against 9.7 ms of GPU (the roads are painted into the ground's texture layers, so
  they cost nothing per frame), the editor 100 fps either way, 90 s of an 8-car melee 1.3 s of CPU. The stock files hold 1 501 roads and 202 000 points over 797
  missions, so a map this size is far beyond anything shipped; the original engine's behaviour at it is untested.
- **Playing it.** `--hour` is the light preset (the stock maps use 0, 6, 9, 15, 21); the look is Nitro's P17 prairie (`tp011gr6` grass,
  `nk_6cld7` sky, `p17_16` palette and horizon, hour 9), checked against the archive (`MapLookCatalog`). Eight spawns and four regen pads are put on the main
  roads, spread by always taking the point farthest from the ones chosen, facing along the road. Tests: `tests/I76.Editor.Tests/GeoMapTests.cs` (projection,
  grid, packing, PNG and terrarium decoding, bicubic sampling, road import, a small map built, saved and read again), `tests/I76.Sim.Tests/GeoMapDriveTests.cs`
  (a car driven the length of a road on a made-up map; with `I76_GEOMAP_MAP=path/to/map.cbt` or `.lvl`, on a real one: the Billings interstate, 8.2 km,
  stays within 4 m of the centre line and never leaves the ground).

### New map from real world (in the editor)

MAP > New map from real world (and REAL-WORLD MAP... in the map list) opens `GeoMapDialog` (`game/Scripts/Editor`): the same build as `i76 geomap`
(`GeoMapJob` in `src/I76.Editor/Geo`, run on a worker thread with progress, cancel and plain error messages) behind a picker, `GeoMapView`, drawn by the game.

- **The picker** shows OpenStreetMap raster tiles, zoom 3 to 17: drag pans, wheel / +/- / bumpers zoom, the arrows and the left stick pan. BOX HERE puts a
  6 km box at the middle of the view; DRAW turns a drag into a new box; the box's eight handles resize it; SMALLER / BIGGER scale it. Its size in km, the
  tiles it needs and whether it fits (254 tiles, `AreaCheck`) are shown and the box turns red when it does not; BUILD refuses a box that does not fit.
- **The tile policy** (`GeoServices`): a User-Agent `I26/<version> (+https://github.com/vahokif/i26)`, tiles cached on disk under `user://cache/osm-tiles/<host>/z/x/y.png`
  for seven days (an older one is shown while it is fetched again), at most two requests at a time, only the tiles on screen (a request for a tile that scrolled away
  is cancelled), a failed tile waits 20 s, a 429 / 403 / 509 stops all requests for a minute, "(c) OpenStreetMap contributors" drawn on the map. The tile URL is
  `GeoServices.DefaultTileUrl` (the environment variable `I26_GEO_TILE_URL` overrides it) and the cache is keyed by its host.
- **Place search** is Nominatim, asked on Enter only (never per keystroke), one request a second (`RateLimiter`), the same User-Agent; the answer is a list and the map flies to the chosen place.
- **The build** downloads into `user://cache/geo` (terrain tiles and the Overpass answer, fetched once) and writes the map files and `<name>_geo.txt` (the area, sources
  and licences: OpenStreetMap ODbL and the terrain datasets) into the install's addon folder, then opens the map. A busy or timed-out road server
  (`OverpassReply` tells them apart from an answer) is retried on the other servers a few times, then reported; a network failure says so and BUILD becomes RETRY (the downloads already made are kept).
- Capture hooks (docs/dev-hooks.md): `I76_EDITOR_SCRIPT=10:geoform`, `I76_GEO_VIEW`, `I76_GEO_BOX`, `I76_GEO_SEARCH`, `I76_GEO_BUILD`.

## The gallery map

`i76 gallery OUT_DIR` makes `gallery.lvl` (base game) and `gallery.cbt` (Nitro Pack) from the installs' object catalogues: every breakable or
drive-on structure (ODEF types 2, 3, 0xb-0xd) once, a 170 m cell each on flat ground, so each has 80 m of clear lane. Drop the files and
`gallery.ter` into a scratch copy of an install's `addon\` folder (never the original) to look at them or shoot them. `GalleryTests`
(tests/I76.Sim.Tests) builds the same map and, per structure, leaves it standing, destroys it with a car's guns (every node, from four sides) and
destroys it whole (`DestroyStructure`), drives a car at it along its ramp direction in each state, and writes the table to `I76_GALLERY_REPORT`.
Run in the original (as a melee mission, the files in the run directory's ADDON) the water tower, crusher hopper,
lean-to and ramp 5 lose the same nodes to the same rounds as in the sim; the ramps with 50000 HP take 50 a round.

## Map style

The top bar's **STYLE** opens a window (`MapLookPanel`) for what the map looks like: the **ground**, the **sky**, the **horizon**
and the **time of day**. Each tab lists everything the install's archive holds with a small picture (made a few a frame, the tab on show
first, kept for the session; an 8-bit one through the palette it brings, below), the stock maps that use it in the tooltip, and the map's
choice highlighted. Click to choose, **APPLY** to put the choices in the map (**REVERT** drops them). It is one undo step
(`LookCommand`; Ctrl+Z puts the earlier look back), the title shows the edit, and Save writes it.

What each is in the map file (WDEF/WRLD, docs/map-format-writing.md; `MapLook`, `MapDocument.Look`). Every one is a field of its own that the
game reads on its own, and the stock missions mix them freely (M05: `t16_16.act` with `t02.lum`; M03: p13's palette with t17's tables):

| Choice | WRLD field | Offered |
|---|---|---|
| Ground | +0x6c, the ground family's level-1 name (`tp041db6.map`; the engine takes the largest member, the nearer mips) | every family in the archive: the 16-bit TP/TN sets, the 8-bit TT sets, M15's asphalt (`MapLookCatalog`) |
| Sky | +0x52, the cloud texture on the dome (`nk_6cld3.map`; the sun and moon sprites are the time of day's) | `nk_6cld0-e` (16-bit), `nk_1cld1-8` (8-bit) |
| Horizon | +0x86, an `.hzd`: the 16 panel textures round the horizon (not in a WREV 8 level) | each distinct panel set once, named by its `_16` list; "none" for no panels (M15 and a few arenas have none) |
| Palette | +0x1e, the `.act` the 8-bit textures are drawn through | not offered: it follows the ground, sky and horizon (below). The `.lum` (+0x2b) and `.tbl` (+0x38) are separate fields and stay as they are |
| Time of day | +0x93, the hour: `((hour + 2) % 24) * 8 / 24` picks one of eight light presets (sun or moon, its angle and strength; night: headlights) | the eight: 0, 3, 6 ... 21 |

**The palette follows** (`MapLookCatalog.Suited`). It colours only the 8-bit textures: every prop, building and car skin, the TP/TN
grounds and the nk_6 skies have 16-bit members, which carry their own colours (the Piranha's skin is the same through every palette).
The archive's 67 palettes share their colours but for 64 (176-239), which are the 8-bit grounds', skies' and horizon panels': the
TT grounds, M15's asphalt, the nk_1 skies and the 8-bit panel sets. Through another map's palette one of those comes out wrong (T04's
grey `tt031sd_` is brown through T01's palette, white through p18's), so picking an 8-bit ground, sky or horizon brings a palette the stock maps use with it: the first
8-bit one of the ground, sky and horizon decides, the map's palette stays if a stock map uses it with that one, else it becomes the one
they use most. With all three 16-bit the palette is left as it is (it changes nothing to be seen). The pictures show each 8-bit one
through that palette.

The names are what stock WRLDs hold, 12 characters at most, the right extension, a ground's fifth character the level digit 1
(`MapLook.Problem`); anything else is refused and never written. The light table (WRLD +0x97) follows the hour in the engine, not
the file (`i76 looks` prints every stock mission's). Not offered: the movie, overview, `.npt`, scrounge and music fields.

**Applying rebuilds the world**, as a playtest's return used to (about 3 s on T01, a loading card): it is built again from the file with the look patched over
its WRLD in memory (`WorldSettings.Patch`, `MissionWorld.RefreshLook`, `EditorSession.Look`), so everything derived from the choice follows exactly as
in a fresh open and in both looks: the ground material and its fitted noise, the roads' blend, the sky, its fog and colours, the 3D
horizon ring and its profile, the sun or moon and the night, the vegetation (a custom or unlisted map grows by its ground family; the campaign's
named maps keep theirs, so T01 on snow still has its desert plants) and the 1997 look's dome. The heights, roads, objects, undo history,
camera and the open window carry across. Nothing is updated in place: the many places the look reaches (shader parameters, the horizon ring built off-thread,
the ground fit, plants) are rebuilt together, not patched one by one. In the 1997 look the editor draws the horizon panels by the original's rule, from the camera's height (they sink out of sight as it rises); the optional Software renderer takes its palette, sky and ground from the mission's `miss8` twin, which a custom map has not.

Saving writes the look through `MapDocument.Look`, only the fields that differ from the file (names compare without case), so a map saved
without touching its style stays byte for byte, and a stock map saved under a new name carries the look. The hook is `style[:tab]`
(open the window) and `mapstyle:ground,tp081sd6,sky,nk_6cld0,horizon,p13_16.hzd|none,palette,t02_16.act,hour,12` (apply at once; without `palette` it follows as in the window), docs/dev-hooks.md.

## Playtest

**F5** drops the player's car under the mouse cursor, upright and facing the camera's heading, and runs the map as a melee with no
opponents (`--melee`); **F5** or **Esc** returns to the editor with the camera where it was. The spot is where the camera's ray through
the cursor first meets the ground, a road or a drive-on structure (a bridge's deck, so the car stands on the deck, not under it):
`PlaytestSpawn.FromRay` (src/I76.Sim/Terrain), run by the playtest's scene on the world it has built, with the sim's own ground and
colliders. The PLAYTEST button and the menu's Playtest item use where the cursor last was over the 3D view, as the cursor is on the bar
when they are clicked. The car settles on its suspension before the first tick, as a mission's does. Where a car cannot stand (the cursor
is on the sky, off the map or at its edge, on ground the map marks impassable: water or a cliff, on or inside a building or other
collider) or the cursor was not over the 3D view (a key press with it on a window, the bar or outside the window), the car goes where it
used to, at the camera's pivot, and a line at the bottom of the playtest's view says why for a few seconds. The editor's camera is kept
for the return.

**Switching fast.** The editor's scene is not thrown away for a playtest: it is put aside, inert and out of sight (`Main.Suspend`: processing
off, its windows and 3D nodes hidden, its world environment out of the tree), a second `Main` scene is added beside it and plays the map, and
the way back frees that scene and takes the editor's back (`Main.Stash`, a `ViewStash` from `I76.Editor`). Going back builds nothing: about
0.05 to 0.1 s (the look and the globals the playtest's nodes reset are set again a frame later, and the managed side collected), against
2 to 3 s for a scene reload. The editor's copy is kept only for the document revision (`TerrainDocument.Revision`, which counts every edit of
the heights, roads, objects and look and never goes down) and the map and install it was made for; anything else frees it and the scene is
reloaded as before. A map streamed in pieces (`Terrain.Huge`) is not kept (it would be held twice), and `I76_EDITOR_STASH=0` switches the
keeping off. The playtest's scene builds its world from the heights, roads, objects and look the editor handed over (`EditorSession.Prepare`),
so an edit made before F5 is in the playtest, and the editor's own document, undo history and objects (never touched by the playtest's
world) are as they were on the way back. Going in builds the mission's world again, but not what the editor session already has: while a session is open (from picking a map to leaving the editor) a `SessionStore` (`I76.Formats`) keeps what the builds decode from the install and make from the heights, bounded at
512 MB (past it the store is emptied and starts over) and dropped when the session ends. In it: the textures (indexed data, RGBA and the
engine's image, per install, map folder and palette), the model parts read from the install, the road layers baked for the map's roads and
textures (also for edited ground, where the disk cache is not used), the 1997 ground's chunk arrays and the modern ground's coarse and
near levels, and the distant terrain's arrays, each keyed by a hash of what it was made from (the terrain's grid and every sample, the
surface's seeds, the panels, the light), not by `TerrainDocument.Revision`: an edit of the heights changes the key, an undo gives the earlier
one back, and the ground of another document never matches. The roads, objects and look edits reach the playtest through the key parts they
change (road layers: the quads; the ring: the palette and light) or are not cached at all (scenery is placed from the editor's object layer
every time). Besides, three small things are made once per run (the oil and fire patches' textures, the drop shadows' shaders) and the
renderer's shadow filter and atlas are set only when the look changes them. Entering a playtest of T01 takes 1.6 s in the modern look and
1.0 s in the 1997 look (3.3 s and 2.2 s before); a 16-tile flat map 1.1 s and 0.7 s (2.4 s and 1.7 s). What is left is mostly the Godot
objects of the world (ground and scenery meshes, the cars and their wrecks, the effects views, the plants), 0.05 to 0.2 s each; the
music fades out meanwhile with a loading card of 0.1 s over it. The effects rehearsal behind the card is skipped from the second playtest of a run in the
same look (the renderer has made those pipelines, `I76_WARM_AGAIN=1` rehearses again). The sim's ground, its colliders, the plants and the
drawn ground are all built from the edited heights. The road layers' baked cache on disk is skipped while the ground is edited so a
playtest's every variant does not fill the user's cache folder.

Leaks the switching showed are closed: a playtest's smoke emitters stayed in the smoke layer's table and the force feedback's `Held`
delegate stayed on the last scene, each keeping the scene, its builder and all it made (some 400 MB of meshes and textures a playtest) alive for the
rest of the run; the cars' tail-lamp glow materials were kept by texture for the run, holding every finished scene's car textures; a scene whose
scenery had no markers, and an editor that never drew a bridge stretch, left a stray node each. The process's memory used to climb with every
playtest or mission restart (10 to 20 MB a cycle) because the freed scene's memory stayed in the C allocator's heap as holes the next
scene's blocks did not fit: now blocks of 1 MB and more are mapped (and returned) directly, and the heap is trimmed after a collection of the
managed side (twice: the first frees wrappers whose native objects hold others) when a playtest returns and before a second mission's build.
`I76_SWITCH_TIMING=1` with `I76_SWITCH_MEM=1` prints the counts that show it, and `I76_REPLAY_AT=N` restarts a mission over and over.

## Saving

`IMapDocument.Save()` goes through the map writers (docs/map-format-writing.md): the mission file is read again, the document's
heights are put into its terrain, and `MapProject` writes both files in the original formats into the install's `addon\` folder.
A map of your own (one already in `addon\`) is written in place. A stock map (miss16, or a folder inside `addon\`) is never written:
Save asks for a new name and a listed name and writes a copy (`MapProject.CloneAs`), and the document is that copy from then on.
New flat maps are `MapProject.CreateBlank` (a Nitro arena's sky, light and palette; `.cbt` in a Nitro install, `.lvl` otherwise) with
a spawn in the middle.

## Code

- `src/I76.Editor` (no Godot): `IMapDocument`, `TerrainDocument`, `BrushStroke` and `BrushMath`, `SurfaceTypes`, `StrokeCommand` and `UndoStack`,
  `MapSaver`, `NewMap`, `MapLookCatalog` (the choices of the map style) and `LookCommand`; `Geo/` (`i76 geomap`: see Geographic maps); `Roads/`: `RoadCurves`, `RoadBuilder`, `RoadGrade`, `RoadTerrain`, `RoadSnap`, `RoadPlanner`, `RoadOps`,
  `RoadEditing`, `RoadCommand`, `RoadStore`; `Intersections/`: `PieceCatalogue` (the stock pieces, arms and top faces), `JunctionRules` (the angles,
  the fit), `Junctions` (detect, cut, settle after edits), `PieceStore`, `JunctionCommand`; `StructureAttach` (the ends of a deck or ramp, shared with the object tool); `Objects/`: `ObjectCatalogue`, `ObjectLayer` and `ObjectCommands`, `Footprint` and `PlaceRules`, `Snapping`, `RoadGeometry`, `RoadSplit`, `MapChecks`. Tests: `tests/I76.Editor.Tests` (brush maths, strokes, undo, saving through the real writers in a scratch folder,
  and that a refreshed `TerrainSurface` draws where a fresh one does; `RoadTests.cs`: curve sampling, snapping, grade limiting, the RSEG
  conventions against the stock roads, extension and loops, edits, conforming, undo, saving). `tests/I76.Sim.Tests/EditorRoadTests.cs`: the AI on a made road, the stock bridges and ramps against the structure ends.
- Paths: `src/I76.Editor/Paths` (`PathStore`: the table, its copies grouped, what may be deleted, labels; `PathEdits` and `PathCommand`:
  move, insert, delete a point, add, rename, delete a path, picking), `src/I76.Formats/Mission/Edit/FsmPaths.cs` (the table in the
  payload), `src/I76.Formats/Fsm/FsmPathUse.cs` (what the script names); `game/Scripts/Editor/Paths`: `PathTool` (input, modes),
  `PathToolUi` (palette group, panel, the `path:` script commands), `PathVisuals` (the layer). Tests: `PathTests.cs` (I76.Editor.Tests),
  `MapPathTests.cs` (I76.Formats.Tests: every stock table written back byte for byte, an edited path saved and read back with the
  script around it unchanged, paths in a map without a script, the analysis over the stock scripts), `EditorPathTests.cs` (I76.Sim.Tests).
- `src/I76.Editor/ViewStash.cs` and `game/Scripts/Boot/MainStash.cs`: the editor's scene kept aside for its playtest (tests: `ViewStashTests` in `EditorTests.cs`, `RevisionCountsEveryEditAndNeverGoesDown`).
- `src/I76.Sim/Terrain/PlaytestSpawn.cs`: the cursor ray to the playtest's spawn point and its refusals (tests: `PlaytestSpawnTests.cs`).
- `game/Scripts/Editor`: `MapEditor` (+ `MapEditorUi`), `SurfaceOverlay`, `EditorCamera`, `EditorPicker`, `FormDialog`, `EditorStyle`, `FitScroll` (the columns that scroll), `EditorSession`, `MapLookPanel` (the map style window); `Roads/`: `RoadTool` (input, state, the panel and the `road:` script commands), `RoadVisuals` (preview, grade ribbon, handles, snap ring), `RoadPainter`; `Intersections/`: `IntersectionSession` (the pieces across playtest reloads, put into the world's placements), `PieceView` (the preview), `MissionBuilder.RefreshPieceTops` repaints the pieces' tops after an edit (tests: `IntersectionTests.cs`: rules, arms in the engine's frame, the junction edits and their undo, the surface, saving, the stock maps against the rules); `Objects/`: `ObjectEditor` (+ `ObjectUi`), `MarkerView`, `Thumbnails`.
- The object surveys behind the catalogue and the snapping rules are in `tools/I76.Cli/ObjectSurvey.cs`: `i76 objects OUT.tsv` (every name, type
  and flag the stock maps place, with counts, kinds of map and model sizes), `i76 facing` (which side of a building the stock maps put
  the road on), `i76 decks` (road points inside each stock bridge, ramp and tunnel), `i76 bridges` and `i76 roadpts MAP` (roads near
  structures, and a map's roads and objects to aim a capture at). They print numbers read from the install; nothing they write is kept in the repository.
- `game/Scripts/Boot/MainEditor.cs` and the editor hooks in `Main.cs`; the shell's entry is in `MainMenuScreen.cs`; the brush cursor is in
  `game/Shaders/brush_cursor.gdshaderinc`, included by the terrain shaders of both looks.
