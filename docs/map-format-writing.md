# Writing maps back out (the editor's foundation)

`I76.Formats.Mission.Edit` reads a map, lets code change fields, and writes it back **in the original formats only**:
the mission file (BWD2: `.cbt .rac .cf2 .cf3 .cf4 .msn .lvl`) and the terrain (`.ter`). Nothing new is invented, so a saved map
loads in I26, the original game and the Nitro Pack alike.
The model is read, edit some fields, write: everything a field does not name is written back as it was read.

## What a map is

| File | Contents | Writer |
|---|---|---|
| mission `.cbt` (melee), `.rac` (race), `.cf2/3/4` (CTF, 2-4 teams), `.msn`, `.lvl` | BWD2: `BWD2`, `REV`, then `WDEF`, `TDEF`, `RDEF`, `ODEF`, `LDEF`, `ADEF` (each a list of chunks) and `EXIT` | `MapDocument` |
| `WDEF` = `WREV` (9; 8 in 1997 `.lvl`), `WRLD` (331 bytes: CD track, movie, palette, `.lum`, `.tbl`, `.npt` text name, sky, scrounge `.sdf`, ground, overview, `.hzd`, hour, 8 x 20 light table, the arena name at +0x13b), `EXIT` | | name, hour, CD track and the look (`Look`: palette +0x1e, sky +0x52, ground +0x6c, horizon +0x86, hour +0x93; docs/map-editor.md "Map style") edited; the rest verbatim (`WorldPayload` for the raw bytes) |
| `TDEF` = `TREV`, `ZMAP` (tile count + 80x80 grid of tile ids, 255 empty), `ZONE` (0xff + the `.ter` name, 13 bytes), `EXIT` x2 | | `Zmap`, `TileCount`, `TerrainName` |
| `RDEF` = `RREV`, `RSEG` roads (kind, count, 24-byte points), `EXIT` x4 | | `Roads` |
| `ODEF` = `OREV`, `OBJ` placements (100 bytes), `EXIT` | | `Objects` |
| `LDEF` = `LREV`, `OBJ` polylines (0x64 header, count, xyz points), `EXIT` | | `Lines` |
| `ADEF` = `AREV`, `FSM` (the mission script), `EXIT` | | `Paths` (the script's nav-path table, `FsmPaths`); the rest of the script opaque, never touched (`FsmPayload` reads it) |
| `.ter` | tiles of 128x128 little-endian u16 samples (0x8000 bytes): height in bits 0-11 (x 0.1 m), surface nibble in 12-15 | `TerrainTiles` |
| `.npt` (mission text), `.hzd` (horizon), `.act`, `.lum`, `.tbl`, `.pcf` (preload), `.map` | text, palettes, textures the WRLD names | not written: a map names the stock ones, or ships them loose beside it |

Spawns, regen pads, race checkpoints and CTF flags are not separate chunks: they are ODEF placements the object loader
recognises by name and type. `MapObject.Role` classifies them: `spawn` / `#spawnN`
(type 1; N is the CTF team), `regen`, `checkN` (N 1-98, `check99` the finish), `a1flagN` (type 2 or 3; N the team). Laps and
capture limits are the game setup's (the lobby's), not the map's. The FSM is opaque but for its nav paths; melee, race and CTF maps carry an empty one.

**Nav paths.** The script's paths are a table inside the FSM payload, after the prototype, actor and sound tables: a u32 count,
then per path a 40-byte NUL-padded label, a u32 point count and that many float x, y, z (world frame; every stock point has y 0,
the game takes the ground's height). The script names a path by its index in the table, never by its label; the script compiler
wrote one copy of a path for each place that names it (P18 has `bridgenav` four times, every copy the same). `MapDocument.Paths`
reads the table; `ToBytes` writes it back into the payload, everything before and after it byte for byte, and only when it changed
(the stock files come back identical). A map with no script (an empty payload) that gets paths is given empty prototype, actor,
sound, thread, data and code tables around them: the game reads an empty payload as those tables with no entries, so the map plays
as before. `FsmPathUse` reads from the bytecode which entries the orders and tests name (goto, race, evade, teleport, the
isWithinNav tests, the cut-scene cameras) where the index is an immediate or a thread parameter, followed through calls.

## What is parsed, what is preserved

`BwdTree` holds every chunk as a node (tag, payload, children, and the bytes after the last child, which no stock file has).
`ToBytes()` re-serialises the tree with sizes recomputed, so chunk order, `EXIT` terminators, unknown chunks and padding are
kept. Typed views sit over copies of the payloads and are merged back slot for slot, in the original position:

- **Objects** (`MapObject`): the 100-byte payload itself. Properties: `Name` (7-bit text, 1-8 chars), `NameFlags` (the name bytes'
  top bits, per-placement data), `Rotation` (3x3 row-major, row 2 is forward), `Yaw` (radians about +Y, 0 faces +Z), `Position`,
  `ObjectType` (+0x5c), `ObjectFlags` (+0x60; 0x10 is the player's car). The 36 bytes at 0x38 are kept untouched (zero in every
  stock file). The engine overwrites each object's y from the terrain at load (docs/formats/mission-files.md), so a y in the file
  is only a hint.
- **Roads** (`RoadEdit`, one per `RSEG`): `Kind` (texture family, kind % 3) and `Points`. A `RoadEditPoint` is the 24-byte record:
  the left edge (x, z), the right edge (x, z), the left y word (holds the point's centre z, not a height: `Move` keeps it the mean
  of the edges' z, true in every stock point) and an integer flag word (0 or 36, kept). The engine cuts a segment into pieces of at
  most 25 points itself; the file holds whole segments. Payload bytes past the last point are kept.
- **Lines** (`LineEdit`): `Name`, `Points`, the header kept.
- **Terrain** (`TerrainTiles`): `SetHeight` (0-4095, tenths of a metre), `SetSurface`, `SetSample`, `AddTile`. Untouched, a `.ter` is
  written as read: a file longer than its tiles keeps its tail, one too short (the pack's `obsid`) stays short unless an edit needs
  the missing tile, and then it is padded to whole tiles.
- **World**: `ArenaName` (16 bytes at WRLD +0x13b, +0x12e in WREV 8), `Hour`, `CdTrack`, `Look` (a `MapLook`: the 13-byte names of the palette, sky, ground and horizon (WREV 9) and the hour; only fields that differ are written, names are checked for fit and extension, a ground's level digit is 1), `TerrainName` (the ZONE; 1-12 characters, no
  folder). Names are validated: the lists and the ZONE field are fixed-size.

Not understood and therefore never changed: the WRLD light table and the other WRLD fields (the `.lum`, `.tbl`, text, scrounge, overview), the FSM script (but its path table), the object bytes at
0x38-0x5b and 0x64+ (none exist in the stock maps), the second `EXIT`s and the chunk revisions.

## The API

```csharp
var p = MapProject.Load(install, "n05");                // or Load(path[, install[, terPath]]); finds the ZONE's .ter
p.Terrain.SetHeight(sx, sz, 777);                       // global sample coordinates: world x/5, z/5 (MapProject.SampleOf)
p.Mission.Roads[0].Points[2].Move(new Vector2(12, -7));
p.Mission.Objects[0].Position = new Vector3(3000, 40, 49000);
p.Mission.Objects.Add(MapObject.Spawn(pos, yaw: 0, team: 0));   // Checkpoint(n, pos), Flag(team, pos), Regen(pos)
p.Mission.ArenaName = "My Arena";
p.SaveToAddon(install);                                 // or SaveTo(dir): <name><ext> and the ZONE's .ter; ToFiles() for bytes

var copy = p.CloneAs("myrift", "My Rift");              // new stem (1-8 of A-Z a-z 0-9 _), ZONE renamed, display name set
var blank = MapProject.CreateBlank("flat1", "Flat One", ".cbt", tilesWide: 2, tilesHigh: 2, height: 100);
```

`MapProject` (mission + terrain as a set: `Name`, `Extension`, `Mission`, `Terrain`, `ToFiles`, `SaveTo`, `SaveToAddon`, `CloneAs`,
`CreateBlank`, `HeightAt`, `WorldBounds`), `MapDocument` (`Objects`, `Roads`, `Lines`, `Zmap`, `TileCount`, `ArenaName`, `Hour`,
`CdTrack`, `TerrainName`, `WorldPayload`, `FsmPayload`, `Paths`, `Spawns`, `Regens`, `Checkpoints`, `Flags`, `ClearContent`, `ToBytes`),
`MapObject` / `MapRole`, `RoadEdit` / `RoadEditPoint`, `LineEdit`, `TerrainTiles` (writers above), `BwdTree` / `BwdNode` (the raw
tree, for anything else).

Adding a tile: `p.Terrain.AddTile(row, col, fill)` sets the grid cell; `ToFiles` copies the count into ZMAP. The grid has room for 254
tiles (id 255 is "empty"); the engine maps the `.ter` and reads tile i at i x 0x8000 with no size check, so a file must be whole.

## Roads, as the stock files have them

Surveyed over both installs' 797 missions (1501 roads, 202 196 points); `RoadBuilder` writes to these, and `RoadTests` checks that it rebuilds
stock roads from their own centre lines to within 35 cm for 97 % of edges.

| | Stock | Notes |
|---|---|---|
| width | 9.996 to 10.004 (every point) | |
| edges | square to the central difference of the neighbours' centres (mean error 0.004 in the dot product); the first point square to its forward step | left = right + 10 x (-tz, tx) for the direction of increasing index: true of 199 171 of 199 172 points |
| spacing | median 5.09, 10th percentile 5.00, 99th 8.3 | 5 is the terrain sample step |
| turn | 99 % of points turn under 8.9 degrees (a radius of 32 at 5 spacing) | |
| left y word | the mean z of the two edges' z (all points) | `CentreZ` |
| right y word (`Flag`) | 0 or 36: the first points 0, then 36 (`00666666...`), a float's bits at some road's last point | the engine overwrites it with the terrain height; nothing reads it |
| kinds | 0 (857 roads), 1 (602), 2 (41), one of 3072 | kind % 3 picks the texture family: asphalt (r2anr / r2ayr, ten textures cycling along the road), dirt (r2dnr), planks (r2wnr) |
| loops | 6 closed roads, the last point exactly the first (or 7 or 10 units short) | |
| grade | per road, the 95th percentile of the steepest steps: median 0.095, 75th 0.19, 90th 0.33 | the editor's default maximum is 0.10 |

Roads and structures (type 0xb ramps and jumps, 0xc bridges, found from the sim's drive-on polygons; T05, M05, T10 and the arenas): a bridge
(`bebridg1-4`, `beovrps1`, 60 to 160 long, 10 wide, laid level with the ground) lies between two roads: both of its ends are ends of
roads, to within a few tenths (the road may run a point or two past). A jump ramp (`aaramp4/5`, 25 long, rising 7, 10 wide at its foot and
24 at its lip) has its foot on a road's end (or on a road); its lip is a drop. `StructureAttach.Ends` derives these ends from the polygons
(the principal axis of the top faces, the end edges' middle and width, the height of the end, the direction out of the structure,
`RoadLevel` for the ends a road can meet) and the road tool snaps to them; the object tool can use them the other way round.

## Intersections (the road-piece objects)

The game has no junction in the road data: every RSEG is a road of its own; `ISEG` and `TSEG` chunks are skipped.
A junction in a stock map is **two things laid by hand**: roads that stop at a gap, and a **road-piece object** (ODEF type 0x50, 0x52 or
0x53, flags 0) standing in the gap, whose upward faces the loader draws over the ground (the port paints them into the road layer:
`RoadRaster`, `MissionBuilder.RoadPieceTops`). `i76 isect models|survey|surface|payload|strip|spawn` (tools/I76.Cli/IsectSurvey.cs) made
the numbers below: 32 base maps (392 placements) and 62 Nitro maps (642); every `.sdf` of the 55 `i*.sdf` pieces.

**The library.** The pieces are flat (y 0), one or two faces, a corner of the model at its origin; the engine stands the object on the
ground at its origin's height (`MissionWorld.GroundedPosition`: the file's y is overwritten), so a piece is a plane at its corner's height.
The `i*isect*` names are the original library's; the maps use them and the `ixa21_1` style, which are the same meshes and textures (same
bounds, faces and texture names: `itisect1` = `ita21_1`, `ixisect1` = `ixa21_1`, `iyisect1-3` = `iga21_1` / `igd27_1` / `igw29_1`,
`iyisect4-6` = `ila21_1` / `ild27_1` / `ilw29_1`, `iyisect8` = `iyd27_1`, `idisect1` = `ija2171`, `idisect2` = `ijd27t1`, `ieisect1` = `ica21_1`).
Name = `i` + shape (`t x g l y j c m d e`) + surface (`a` asphalt, `d` dirt, `w` planks) + `2` + the look (asphalt 1, 2 or 3: the middle
digit, a colour variant that a map keeps throughout: M03 and M04 use 1, S07 2, T01 3; dirt 7, planks 9) + `_1`/`_2` (`_2`: no yellow line). Kinds in the
files: type 0x50 asphalt (262 + 393), 0x52 dirt (128 + 242), 0x53 planks (2 + 7). Every one of them has a yaw of 0, 90, 180 or -90 but 8 of 1034.

| shape | size (x, z) | arms (local frame, outward heading from +z toward +x) | what it is |
|---|---|---|---|
| T `ita2n_1` `itd27_1` | 10 x 10 | (5,0) 180, (5,10) 0, (10,5) 90 | a through road along z, a stem to +x |
| X `ixa2n_1` `ixd27_1` | 10 x 10 | the same and (0,5) -90 | two roads crossing at right angles |
| fork right `iga2n_1` `igd27_1` `igw29_1` | 10 x 25 strip + a stub to 23.8 | (5,0) 180, (5,25) 0, branch (20,20) 41 | the through road with a branch leaving forward at 41 degrees on the +x side |
| fork left `ila2n_1` `ild27_1` `ilw29_1` | the mirror, strip at x 15-25 | (20,0) 180, (20,25) 0, branch (5,20) -41 | the same on the left |
| split `iyd27_1` (dirt only) | 30 x 45 | stem (15,0) 180; (5,45) 0 and (25,45) 0 | a road splitting into two parallel ones 20 m apart |
| J `ija2n71` (asphalt) | 10 x 20 | (5,0), (5,20) | a straight 20 m strip: in the samples read it lies on the stem side of a T, with the stem's road (often a dirt one) ending at its far edge (5,20) |
| end caps `ijd27t1`, `ijw29t1` | 10 x 10, one edge slanted | (5,0) | the end of a dirt or planks road |
| round ends `ica2n_1`, `ieisect1` | 20 x 20, an octagon | (10,0) | a turn-around at an asphalt road's end |
| `ima2n_x` | 10 x 10 | | a filler of the Nitro race tracks, two roads side by side |
| `rmrwmid1`, `rerwend1-4` | 20 x 200, 20 x 25 | | runways |

The fork's stub is a parallelogram whose edges lie at 41 degrees (the stock roads leave at about 45); its centre line meets the strip's
axis 2.73 along the strip, which is the point the editor puts on the through road. There is no T or X piece for planks (the only planks
pieces are the two forks and the end cap).

**The roads at the arms** (784 stock T, X and fork pieces with a road at every arm, both installs): a road ends **on the middle of the
piece's edge**, within a tenth of a metre, heading straight along the arm's axis for its last 5 to 10 m (the last points are 5 apart and
collinear). The through road is **cut**: two roads, each ending at one of the arms, the gap between them exactly the piece. A branch
road of a fork starts 0 to 10 m beyond the stub's edge, usually 5 (at (23.5, 23.5): the end of the stub is (20, 20)). 88 % of all arms
have a road end within 1.5 m (the rest are dead ends: a T with two roads, a stem that was never built). A road meeting a piece of the
other kind is fine (a dirt stem on an asphalt T is common): the piece is the **through** road's kind.

**The ground under a piece.** The ground's surface nibble (`.ter` bits 12-15; the sim's `SurfaceId` is bits 13-15, bit 12 is the
impassable flag) under the stock pieces is **0xc** (asphalt) or **0xe** (dirt and planks) over 75 % of the samples in the footprint (the rest 0x4, the road's own); under
roads it is 0x4 (asphalt), 0x6 (dirt) and 0x8 (planks). Ids 6 and 7 are rows of the world's surface table (the original's force feedback calls them
"parking lot" and "dirt intersection"), with their own grip, rolling resistance and bump; the editor's roads lay the road nibble (below) and the pieces it places
give the samples whose ground they cover (every sample that is the nearest to a point of the footprint: 3 x 3 for a T) the same 0xc or 0xe.

**The ground under a road** (`i76 roadsurf`: every stock sample within 20 m of a road centre line, by distance to the nearest road and road kind;
32 base and 62 Nitro maps, 1500 roads). The nibble is **0x4** under kind 0 (asphalt), **0x6** under kind 1 (dirt), **0x8** under kind 2 (planks);
99 % of the samples within 10 m of the centre line carry it (the road is 10 m wide, so the band is the road plus 5 m a side), about 90 % at 10 to
11 m, 60 % at 11 to 15 m (the band is wider on some maps), 5 % past 15 m (the background is 0, with 1 to 3 scattered). It stops at a road's end
(under 5 % in the sample 2.5 to 5 m past it) and runs on under bridges, ramps and tunnels (0x4 on 1985 of 3000 samples within 2.5 m of a road near
one; the rest is the piece's 0xc / 0xe). Bit 12 (the 0x1000 flag the AI reads as impassable) is never set under a road: the odd nibbles that turn up
(1 to 5 per cent, mostly 5 and 7) are the rough ground around. The editor's `RoadSurface` lays the 10 m band, 2.5 m past the ends, never over a
piece's 0xc / 0xe, and clears bit 12; a bulldozed or moved road leaves the commonest nibble of the ground beside it.

**What the stock game does with them.** Nothing found beyond drawing them and the surface above. The AI reads the RSEG list only (a route is
a walk along one road), the terrain's flag 0x1000, and the ground's surface type under a car (special-cased for a few types in the
back-away test). The pieces are objects of a type none of the sim's lists (damageable 2, 3, 0xb, 0xc, 0x5c), the AI or the collision code
takes. So an intersection is a painted square and a different grip: an AI route never crosses from one road to another, and the editor keeps its hint
("AI cars drive one road at a time"). Not established: whether the extra surface rows matter to the AI's driving beyond grip.

**Rules the editor uses** (`I76.Editor.Intersections`): where a road ends on another road's middle (within 1.5 m of its centre line, at
least 6 m from its ends), the angle between the stem and the through road picks the piece: within 15 degrees of square, a T; within 10
degrees of 41, leaning forward along the through road, a fork (right or left by the side); any other angle, none. Two roads that cross
within 15 degrees of square get an X. The look is the road's kind (`kind % 3`; asphalt in the map's most common look, 1 when it has no pieces).
Stock reproduction (`StockIntersectionTests`): taking the 784 stock pieces apart (the through roads joined, the stem run on to the junction
point) and asking the rules what to place gives the same piece shape 784 times and the same pose (within 3 m and 5 degrees) 784 times.

## New maps

- **Blank**: `CreateBlank` builds the chunk tree from scratch: the standard chunk skeleton and the WRLD of a stock Nitro arena (sky,
  palette `p18_16.act`, text `m01.npt`, horizon `p18_16.hzd`, light table; all in the archive), an empty ODEF/RDEF/LDEF, an empty
  script, and flat terrain at the given height. It sits at grid cell row 72, column 4 (the stock arenas are near row 76, column 4). It has
  no spawns: add some before playing (a melee with none leaves you nowhere).
- **Clone**: `CloneAs` copies mission and terrain, sets the ZONE to `<new>.ter` and the display name, and lowercases the extension. The
  clone shares the source's `.npt`, `.hzd` and the like by name, so a source whose own `.npt` is loose needs it copied beside it.
- The map's name is at most 8 characters (letters, digits, underscore); the listed name at most 15.
- `.lvl` is what the base game lists from `addon\`; `.cbt .rac .cf2-4` are Nitro's. The 331-byte WRLD (WREV 9) is what both GOG
  executables read; a WREV 8 `.lvl` (I76EDIT's) is kept as it is and its name is read from its own offset.

## Round-trip coverage

`tests/I76.Formats.Tests/MapWriterTests.cs` (needs I76_INSTALL, I76_NITRO_INSTALL, I76_CUSTOM_MAPS, the last read only): all 797 mission
files (both stock installs, the community Nitro pack's `miss16`, `miss8` and `addon`) and all 767 `.ter` files write back byte for byte, and
so do the 791 terrains read through the mission that names them, short or long ones included. No exceptions. The edit tests change
a height and a surface nibble, move a road point and an object, add and remove placements and road points, and check that the bytes
that differ are the named ones and that everything else (including the FSM and chunk order) is unchanged, through a reload.

## Real-game check

An edited copy of the Nitro melee `n05` (named `ntest`: 40 m hills ahead of every spawn, six road points moved, an object moved, the
display name "Edit Test") written to a scratch install's `addon\` loads and runs in the real Nitro Pack under Wine (as a melee on the Piranha, 353 frames, no faults) and the hill is in
the picture where the stock map has flat ground. The Nitro Pack needs the file name with its extension for a `.cbt`
(`ntest` alone asks for `ntest.msn`), and a melee needs a chosen car, or it dies calling a null pointer.

I26 (`tools/shot.sh --mission ntest.cbt --melee --drive` on the same scratch install) loads the same files and draws the same hills.

An editor-made map with a dirt T and X (`isect2`: a 4-tile flat map, one road, a road ending on it, one crossing it; the pieces `itd27_1` and `ixd27_1`
at 0x52) loads and runs in the real Nitro Pack under Wine, as it does in I26: no fault but the exit-time one every run has (`anet2.dll`), and in the cockpit view the
junction squares are drawn over the ground where the same map with its pieces stripped (`i76 isect strip ... pieces`) shows only the roads. Two things about running it on Nitro:
the original game's teleport does not work there (the run dies calling a null pointer), so the car is put at the junction by moving the map's
spawn (`i76 isect spawn IN OUT x z yaw`); and an asphalt road is far paler than the dirt one at 640 x 480 on this map's snow palette, so the dirt pieces are the ones that show.
