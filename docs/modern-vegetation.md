# Modern look: procedural vegetation

The original places almost no plants (saguaro props in T01 and M01, nothing else). The modern look adds
region-appropriate vegetation, generated in code from no game data and no downloads. It is visual only:
no collision, nothing the simulation or a network game reads, hidden in the 1997 look, and a row in
[deviations.md](deviations.md). Each mission gets a region ([Regions](#regions)): the trip follows the real road
from the South Plains of west Texas over Highway 380 into New Mexico, across the Pecos and south to Fort Davis in
the Davis Mountains, so its plant communities change along the way; the melee arenas get theirs, and anything
unlisted (custom maps, the Nitro Pack's) grows by its ground family. Snow and asphalt grow nothing yet. Where the
land suits them a few tumbleweeds roll before the wind ([Tumbleweeds](#tumbleweeds)).

| Where | What |
|---|---|
| `src/I76.Formats/Vegetation/` | the pure part: L-system and turtle, species, meshes, baked occlusion (`PlantShading`), atlas, biome table, masks, placer |
| `game/Scripts/World/Vegetation.cs` | `VegetationNode`: meshes into Godot, chunks, MultiMeshes, LOD, shadows, the shade on the ground |
| `game/Shaders/vegetation.gdshader` | the one material: atlas alpha test, wind, distance dither, dust, occlusion, thin blades kept a pixel wide |
| `game/Shaders/terrain_modern.gdshader` | the sward: the ground between the plants tinted toward dry grass by the sward map |
| `game/Shaders/vegetation_shade.gdshader` | the shade under the plants, multiplied onto the ground |
| `src/I76.Formats/Vegetation/Tumbleweeds.cs`, `game/Scripts/World/Tumbleweeds.cs` | the rolling tumbleweeds: the wind, their motion (`TumbleweedField`), drawing them (`TumbleweedNode`) |
| `tests/I76.Formats.Tests/TumbleweedTests.cs` | where they roll and how many, the ring they start on, rolling contact, slopes and lulls, hops, walls, shrubs, bursts, determinism |
| `tests/I76.Formats.Tests/VegetationTests.cs` | determinism, sizes, atlas coverage, occlusion and ground shade, placement rules, masks, regions and the ground-family fallback, T11 on its terrain, the sward (T02's map: its time, cover, agreement with the placer, objects) |

## The setting

Graphic Detail (the shell's Options pad and the in-mission pad, row 8) has **Vegetation: Off / Low / High**, Low by
default (`ShellSettings.Vegetation`, `[port] vegetation`, settings version 10: older files get Low). It applies at once in a mission. `I76_VEG=0|low|high` overrides it ([dev-hooks.md](dev-hooks.md)).

| | Low | High |
|---|---|---|
| density (of the biome's cover) | 0.6 | 1.0 |
| shrubs fade out over | 120–160 m | 190–250 m |
| grasses and forbs fade out over | 35–50 m | 60–85 m |
| middle / far meshes beyond, shrubs | 20 / 50 m | 30 / 70 m |
| middle / far meshes beyond, grasses and forbs | 7 / 22 m | 11 / 35 m |
| sun shadows from shrubs within | none | 60 m |
| shade on the ground under plants fades out over | 35–50 m | 55–75 m |

Low's plants are a subset of High's (the same cells, a lower cover threshold), so switching only thins them.

## Regions

`Biomes.For(mission, ground)` gives a mission its `VegRegion`: a biome, whether its low ground is a strip of its own
(`LowGround`: T11's river bottom, where the riparian species grow, and M10's salt flat, which stays bare), and a
factor on the cover. Every other map's low ground is upland like the rest: riparian species only grow where there is
a real riverbed. Missions not in the table go by their WDEF ground family (`WorldSettings.Ground`, `tp041db6.map`:
the kind is letters 6-7): `db`, `dr`, `sd` (desert) the desert mix, `gr` (grass) the prairie, `sw`, `sr` (snow) and
`as` (asphalt) nothing (winter comes later).

| Region (biome) | Missions | What grows | Cover per cell: shrubs (4 m) / ground layer |
|---|---|---|---|
| South Plains: shortgrass prairie | T01-T03, T05-T09, S01, S02, S04 (Seminole), S05, S07; A01 at half density; M05, M11, M12 by ground | blue grama and buffalograss everywhere; honey mesquite (fewer, sometimes in mottes), soapweed yucca, sand sagebrush, a little prickly pear and cholla; snakeweed, tumbleweeds and prickly poppy by the roads; Indian blanket and horsemint here and there | .09 / .62 (1.5 m cells) |
| Sand country of south-eastern New Mexico | T10 (Tatum), S06 (the state line) | shinnery oak in low clonal thickets on the sandy flats, sand sagebrush, mesquite, soapweed yucca; grama, sand dropseed | .20 / .45 |
| Pecos: Chihuahuan scrub | T11 (with its river bottom), T12, S03 (the oilfield); M02, M07, M08, M09; M01 by ground | creosote flats, saltbush, mesquite, cholla, prickly pear, yuccas, grama, tobosa, snakeweed; on T11's river bottom salt cedar, coyote willow, alkali sacaton | .26 (.65 river) / .45 (.60) |
| Trans-Pecos | T13, T14 | creosote; on rocky slopes ocotillo, sotol, lechuguilla in colonies, candelilla; Torrey yucca, prickly pear; tobosa, chino grama | .26 / .42 |
| Davis Mountains | T15-T17; M13 (Pyrite Mining) | grassland of sideoats and blue grama and little bluestem; gray oak shrubs, sotol, beargrass, agave on the slopes; prickly pear; a little creosote on the low flats | .20 / .70 (1.5 m cells) |
| Dream | T04 | a sparse stylised garden: flowering ocotillo, soaptree yucca, beargrass, agave, sotol over drifts of Indian blanket, horsemint and prickly poppy, in grey like the land (the plants take the ground's saturation) | .06 / .25 |
| South Plains gone to weeds | M06 (deserted town) | the South Plains' shrubs; snakeweed, tumbleweeds, prickly poppy everywhere, less grass | .09 / .50 |
| Roadside weeds | M03 (airport); not M15, a painted parking lot (asphalt: nothing) | snakeweed, tumbleweeds, grama off the runways and roads; no shrubs | 0 / .22 |
| Suburb | M04 | weeds at the lawns' edges (buffalograss, grama, snakeweed, Indian blanket) and a few planted shrubs (soaptree, prickly pear, agave, sotol, beargrass, mesquite) | .04 / .20 |
| Salt flat | M10 ("It's salty. It's flat.") | nothing on the flat (its low ground, the bottom 1.5 m); saltbush and pickleweed round its edges and on the higher ground | .08 / .20 |
| Desert mix (fallback) | unlisted maps on a desert ground | creosote, mesquite, soapweed, sand sage, saltbush, prickly pear, cholla; grama, tobosa, roadside weeds | .14 / .35 |
| Prairie (fallback) | unlisted maps on the grass ground | little bluestem, sideoats, grama, buffalograss, forbs; a few soapweed and sand sage | .04 / .60 |
| none | M14 (Jorczak's Peak, snow); unlisted snow and asphalt maps | | |

The melee arenas the notepads describe get their own; the bowl (M01) and the desert tracks and highway (M05, M11,
M12) go to the South Plains or the Pecos by their ground family (the campaign's 02, 03 and 05 grounds are the
Pecos', the others the South Plains').

## Species and how each is grown

Real-world adult sizes; each instance is scaled by a species range (about 0.8–1.25) and turned at random. Every
species has 3 variants (creosote, shinnery oak, ocotillo and lechuguilla 4), each built from its own seed at three details: the full mesh, a middle one
(half the foliage clusters, a third for the leafiest crowns, each larger; thin twigs and most of the yucca's skirt
left out; fewer tube sides and blades) and a far one (a quarter to a sixth of the clusters, only stout stems; the
grasses become crossed cards of a painted tuft from the middle detail on).

### Turtle and grammar

`LSystem` is a bracketed stochastic L-system (ABOP ch. 1.7–1.10): each bare symbol is rewritten by one of its
productions, chosen by weight from a seeded stream; symbols carrying a parameter, `+(30)`, are finished turtle
commands and are copied. `Finish` rewrites what is left after the last step (an apex that never grew ends as a
leaf). `Turtle` reads the string (ABOP ch. 1.5): `F(l)` stem segment, `J(l)` cactus joint, `P(l)` pad, `L(s)` foliage
cluster, `K(s)` fruit or flower, `f(l)` move, `+ -` yaw, `& ^` pitch, `\ /` roll, `$` level, `[ ]` push/pop,
`! ' ~` scale radius, length and leaf size. Turns are jittered, steps lengthened or shortened a little, and after
each segment or pad the heading bends toward a tropism vector (up for most, down for the arching tamarisk and
the drooping mesquite). An axiom `Fan(n, lean)` starts n stems round the base at the golden angle, each leaned at
random (optionally from points spread over the ground: the willow's clone, the tamarisk's broom).

| Species | Grammar (after the fan of stems) | Turtle |
|---|---|---|
| Creosote bush (*Larrea tridentata*), 1–1.4 m | 5–8 stems leaned 8–34°: `B → F'[+(26)!B]/(90)B (.24) \| F'[-(26)!B]/(90)B (.21) \| F'B (.25) \| F[&(55)~L]'B (.30)`, finish `B → F[&(35)L]L`, 4 steps | thin (11 mm), upward tropism: an open vase of grey stems, small dark-olive leaf clusters toward the ends |
| Four-wing saltbush (*Atriplex canescens*), 0.8–1 m × 1.5 m | 9–13 stems leaned 25–70°: `B → F[+(38)!'L B]'B \| F[-(38)!'L B]/(90)'B \| FL'B`, finish `B → L`, 3 steps | strong upward tropism curls the stems into a mound; dense grey-green clusters with beige fruit |
| Honey mesquite, shrub form (*Prosopis glandulosa*), 1.7–2.1 m × 3 m | 3–6 stout stems leaned 28–55°: `B → F+(17)[&(42)!'B]F-(17)~L'B \| F-(17)[&(38)/(180)!'B]F+(17)'B \| F+(15)LF-(15)'B`, finish `B → [&(25)L]F(0.12)L`, 4 steps | zig-zag at every node, downward tropism droops the ends; feathery bipinnate foliage |
| Tree cholla (*Cylindropuntia imbricata*), 1–1.3 m | trunk `F F !(0.75)` then `C → J'/(137)[&(58)C]/(137)[&(62)C] (.34) \| J'/(120)[&(60)C]/(120)[&(60)C]/(120)C (.24) \| J'/(90)[&(65)C]C (.32) \| J'K (.10)`, 5 steps | joints off joints in whorls, turning up; tubercled skin with pale spine tufts, yellow fruit |
| Prickly pear (*Opuntia phaeacantha*), 0.5–0.6 m × 1–1.5 m | 3–5 base pads leaned 28–62°, `Q → [+(32)/(22)P Q] \| [-(32)\(22)P Q] \| [+(36)/(18)P Q][-(36)\(18)P Q] \| [+(10)K][-(25)K] \| ε`, 4 steps | pads grow off a pad's rim in its plane (yaw about its normal), rolled a little, bent toward upright; purple fruit |
| Salt cedar, shrub (*Tamarix chinensis*), 2–2.4 m × 3.5 m | 8–11 stems from a 0.35 m patch, leaned 6–30°: `B → F[+(30)!'BL]'B \| F[-(30)!'BL]/(90)'B \| FL'B`, finish `B → LF(0.15)L`, 4 steps | slender reddish stems arching under feathery grey-green sprays, some with pink plumes |
| Coyote willow (*Salix exigua*), 2.8 m | 7–11 stems from a 0.9 m patch, leaned 0–18°: `A → F[+(28)L]/(137)'A \| F[-(28)L][+(20)/(90)L]/(137)'A \| F[+(35)!F(0.2)L]/(137)'A \| FL'A`, 6 steps | a clonal clump of near-upright stems with narrow grey-green leaves along them |
| Sand sagebrush (*Artemisia filifolia*), 0.9 m | 9–13 stems leaned 10–42°: `B → F[+(30)!'L B]'B (.36) \| F[-(30)!' B]/(90)'B (.36) \| F'B (.28)`, finish `B → [&(20)L]L`, 3 steps | upward tropism rounds it; feathery tufts of thread-like silvery leaves |
| Shinnery oak (*Quercus havardii*), 0.9 m × 2.6 m | 14–22 stems off a 1.2 m patch of rhizomes, leaned 10–42°, each `F(0.06)L A`: `A → F[+(48)!'L A][&(60)L]'A \| F[-(48)!'L A]/(90)L'A \| F[&(55)L]L'A (.4)`, finish `A → [&(30)L]L`, 3 steps | a knee-high clone, leaves from the base up: glossy lobed leaves, a few pale undersides; placed in mottes it makes thickets |
| Gray oak, shrub form (*Quercus grisea*), 1.6 m | 4–7 crooked stems off a 0.35 m patch, each with a low leaf cluster: `B → F[+(38)!'B]/(137)~L'B \| F[-(38)!'B]/(137)'B \| F[&(55)~L]+(18)'B \| F[+(32)!'B][-(32)!'B]'B`, finish `B → [&(30)L]F(0.1)L`, 4 steps | a scrubby crown of small dull grey-green leaves |
| Ocotillo (*Fouquieria splendens*), 2–5 m | 8–20 canes out of a 0.25 m crown, each leaned 3–24° and its own length (`'(0.6–1.15)`): `C → FLC (.96) \| F[+(16)!(0.8)FLC]LC (.04)`, finish `C → FL`, 9 steps | slender tapering canes (2.2 cm to 1 cm), barely bending; in leaf (6 in 10) tufts of small leaves at every node, else bare grey; half flower: a scarlet spike at most tips |

### Parametric species

| Species | Built as |
|---|---|
| Soaptree yucca (*Yucca elata*), 2–3.5 m | a trunk 0.4–1.4 m with a skirt of hanging dead leaves; a head of ~96 stiff 3 cm blades 0.8 m long spread over the sphere from 50° below the horizon up (a Fibonacci spiral; shorter toward the top, the lowest dead-coloured); old plants branch into two heads; sometimes a 1–1.8 m flower stalk with a panicle of creamy bells |
| Soapweed yucca (*Yucca glauca*), 0.5 m (1 m in flower) | the same rosette on the ground, ~56 narrower blue-green blades, a shorter stalk |
| Blue grama (*Bouteloua gracilis*) | the bunchgrass generator ([Bunchgrasses](#bunchgrasses)): a low curly tuft of ~70 blades to 0.25 m, grey-green going straw, and its "eyebrow" seed flags on 0.3–0.45 m stems |
| Tobosa (*Pleuraphis mutica*) | a coarser, stiffer, more upright tuft, 0.4 m, half of it grey dead stuff |
| Alkali sacaton (*Sporobolus airoides*), riparian | a big pale tussock of ~90 blades, 0.7 m, with airy panicles to 1.1 m |
| Russian thistle, tumbleweed (*Salsola tragus*) | 2–4 crossed alpha cards of a round tangle, 0.5–0.8 m, straw or green-red |
| Broom snakeweed (*Gutierrezia sarothrae*) | 2–3 crossed alpha cards of a dome of fine stems with yellow flower heads |
| Torrey yucca (*Yucca treculeana*), 2–3.5 m | the soaptree's generator with a 0.9–2.1 m trunk shagged with dead leaves down most of it, ~140 wider (5 cm), keeled blades 0.85 m long in its head, often two heads, a short dense panicle |
| Sotol (*Dasylirion leiophyllum*), 1 m (to 4 m in flower) | a ball of 200 long thin blades (0.65–0.95 m, 3 cm) from a hidden 0.3–0.5 m trunk, radiating every way from 27° below the horizon, the lowest dead; 3 in 10 carry a 2–3.8 m pole with a narrow straw-coloured spike on its upper half |
| Lechuguilla (*Agave lechuguilla*), 0.4 m | a colony of 1–4 rosettes 0.25–0.5 m apart, each of 26 stiff upcurved yellow-green blades (keeled, toothed, a dark spine tip); now and then a 2–3 m flower stalk |
| Havard agave, century plant (*Agave havardiana*), 0.6 m × 1.3 m | 40 broad thick blue-grey leaves in a golden-angle spiral, the outer ones spread low and curving up, the inner ones closing to a bud; toothed margins, dark terminal spines (a painted blade with clear margins and dark teeth); 1 in 10 sends up a 3–4.5 m stalk with yellow flower clusters on side branches |
| Beargrass (*Nolina microcarpa*), 1 m | a fountain of 120 narrow (1.3 cm) leaves 0.9–1.3 m long arching out and over from a tight base; 3 in 10 with a creamy flower plume |
| Candelilla (*Euphorbia antisyphilitica*), 0.3–0.55 m | a clump of 40–64 pencil-thin waxy grey-green stems (3-sided tubes, slightly bent) from a 0.16 m patch; crossed cards of a painted clump from the middle detail |
| Buffalograss (*Bouteloua dactyloides*), 0.1 m | a turf 0.55–0.8 m across of 11–15 little tufts on its runners, each of 10–14 short blades curling out and over, a third of them straw; wide low crossed cards of a painted mat farther away |
| Sand dropseed, chino grama, sideoats grama, little bluestem | the bunchgrass generator ([Bunchgrasses](#bunchgrasses)): dropseed 0.3 m blades with 0.45–0.75 m panicles; chino grama a low curly tuft with two eyebrow flags a stem; sideoats 0.5–0.8 m stems with 14 small spikes hanging from one side; little bluestem an upright blue-green to bronze bunch, 0.5 m, with silvery fluffy seed heads to 0.95 m |
| Indian blanket (*Gaillardia pulchella*), prickly poppy (*Argemone polyanthemos*), purple horsemint (*Monarda citriodora*), pickleweed (*Allenrolfea occidentalis*) | 2–3 crossed upright cards of a painted plant: red rays tipped yellow round a dark disc; white cups on lobed blue-green leaves; tiered pink-purple whorls; green-to-red beaded succulent stems |

### Bunchgrasses

A user found the tufts weird: straight, stiff, uniform hair-thin blades fanning from one point (a shaving brush stuck
in the dirt) in one dark blue-grey green, seed heads floating as dots on invisible stems, identical tufts dotted evenly
over bare dirt, and the far ones breaking up into sparkle. Shortgrass in west Texas and New Mexico is a patchy sward
of straw, tan and grey-green; a bunchgrass is a crown of old dead leaves with blades of every length rising out of it,
most arching over under their weight. So `Grass` builds each variant as one tuft (4 in 10), a big one with one or two
small ones beside it (35 in 100), or a little group of three, and each tuft as:

- **the blades** (`GrassForm`: count, length, width, how upright, the living and dead colours, the dead share, the
  crown): three in five a cushion of short blades (0.28–0.65 of the length), the rest long ones (0.7–1.15) arching out of
  it, each a parabola from a point in the crown (the inner ones steep, the outer ones lower and bending over more, more
  for the longer blades and the less upright species), twisted and curled a little to the side, 4 segments (the
  cushion's 3); 13 in 100 lie over on the ground. Dead blades (the species' share, more of them outside, and all the
  lying ones) are straw, two in five greyed with age; the living ones grey-green with paler dry tips.
- **the thatch**: half as many again short, broken, grey-brown leaves packed round the base, wider, at every angle.
- **the flowering stems** (tufts at half size or more): slender, arching a little under the head, the seed heads at
  their tips (grama's flags, sideoats' spikes, bluestem's tufts, the panicles, painted stout enough to hold together
  down the mips), so none float.

Blades and stems are 8–12 mm wide (real ones are 1–3 mm, too fine for the screen) and carry their side for the shader
(`PlantMesh.Side`, `CUSTOM0`): far off each is widened to `min_px` (0.8) pixels and, so the tuft keeps its coverage
rather than thickening into a blob, only the share of blades its true width pays for is kept, the others narrowed away
by each blade's random (their mean half-width against the pixel's; widening and thinning grow together with distance,
so nothing pops; the flowering stems are widened but never thinned, so their seed heads stay on them). Without it
the blades break into sub-pixel sparkle beyond about 5 m. They are shaded as foliage (flag
0.6, `Shapes.ThinBlade`: the cards' 0.12 cover in the occlusion grid, and a tuft's cards take their occlusion from its
blades) and translucent (`BACKLIGHT`).

The middle and far tiers are 3 and 2 (1 for a small tuft; the smallest left out) crossed cards per tuft of the painted
`GrassTuft` cell: a crown of thatch, a cushion, long blades arching out, straw and grey-green (hues about the mesh's
colour, which carries the species), filling the cell; each card sized to its tuft's full mesh (2.1 times the 90th
percentile of its blades' reach across, 1.1 times the 95th percentile of their height up) and split down the middle
so the dark of the tuft's foot is a vertex of its own (`Shapes.SplitCard`).

### Meshes and atlas

Stems are tubes (3–6 sides by thickness; a stem going on shares the last segment's ring), cactus joints bulged,
wobbled and capped, pads flattened ellipsoids, blades tapered strips, foliage two crossed alpha cards per cluster
(a third across the twig for upright twigs, full mesh only) whose normals point out of the crown so the clusters
shade as one rounded mass. Blades' normals likewise bend from the blade's face toward the direction out of their
tuft (a point below the base) or head (a yucca's or sotol's centre), so a rosette is light on its sun side and dark
on the other rather than every blade lit as flat ground. Per vertex: the sway weight (0 at the ground, 1 at the
top), a foliage flag, and the baked ambient occlusion in the colour's alpha ([Light and shade](#light-and-shade)).
Full meshes are 400–3000 vertices, the lighter ones about 40% of that.

`FoliageAtlas` paints one 2048 × 1024 RGBA texture in code, 8 × 4 cells of 256²: bark, the foliage sprites
(creosote's paired leaflets, mesquite's bipinnate leaves, saltbush, tamarisk sprays with pink plumes, willow leaves,
the tumbleweed tangle, yucca flowers, snakeweed, a grass panicle, sand sage's threads, shinnery's lobed and gray
oak's small leaves, ocotillo's leaf tufts and scarlet spike, the three wildflowers, pickleweed, a candelilla clump),
cholla and prickly-pear skins, neutral blade, grass, dead-leaf and buffalograss-mat cells, and the agave blade (clear
margins with dark hooked teeth, a dark tip) and the pine's needle spray (a twig and side twigs, each a bottle-brush of short dark blue-green needles, paler new growth at the tips; 40% cover). Three cells are free. Clear texels take the cell's mean colour; mip levels are alpha-weighted and, per foliage cell,
their alpha rescaled so each level covers as much at the 0.5 cut as the full-size cell (alpha-tested foliage
otherwise thins away with distance).

## Placed trees

The original places a few trees as flat cards that turn to face the camera: Seminole's 24
pines (`npinetr1`; `npinetr2` is unused) and the unused scrub and Joshua trees (`ntree1`-`ntree4`). The modern look
draws real trees there instead (`I76.Formats.Vegetation.PlacedTrees`, `game/Scripts/World/PlacedTrees.cs`); the 1997
look keeps the turning cards. They are part of the map, not of the setting: shown at Off too, built and tinted the
same, and nothing places procedural plants for them (the placement's footprint masks the plants round it as any
object's).

- **The pine** (`PlacedTrees.Pine`) is grown to the card: as tall as its top over the placement (8.0 m), its crown
  0.85 of the card's width at its widest (3.0 m; `NG_2HV_1`'s crown fills 0.81, `NG_2HV_2`'s 0.94). After the card's
  painting: a bare trunk up the lowest 14-19% (a 7-sided bark tube, 0.28 m at the foot, a little crooked), then
  loose whorls 0.36-0.52 m apart of 4-7 branches, each at its own height (+-0.16 m), bearing and length (0.7-1.3 of
  the crown's envelope there: 0.42 of the widest at the crown's foot, widest 40% up it, narrowing to a spire), one
  whorl in eight missing and one branch in five. A branch leaves the trunk angled down below, out in the middle and up
  near the top, sags through its middle and turns its tip up (a quadratic sweep, tubes of 3 pieces); the needles are
  tufts of 2-3 crossed sprays leaning their own ways along its outer 60%, a little up, bigger toward the tip; short
  shoots off the trunk fill the inside, and a leader tops it. The foliage's normals point out from the trunk at their
  own height (from one centre point, the top's sprays had faced the sky and come out pale, the bottom's dark). The
  middle detail draws fewer, bigger tufts and only the long branches' tubes, the far one a tuft or two a branch on the
  trunk alone (about 4k, 2k, 1k vertices). Three variants, picked by the placement's position.
- **Scrub and Joshua trees** become the honey mesquite and the soaptree yucca, scaled to the card's height.
- **Drawing**: each definition's placements are a MultiMesh per variant and tier, at the placement's position and yaw,
  in the vegetation's material (atlas, wind, the ground's tint) with its tier cross-fade at 30 and 70 m and no fade
  with distance. They cast the sun's shadow (not the drop shadows). The spawner hides a tree with its object: a
  destroyed pine leaves the original's stump, a small untextured card that turns to the camera in both looks.
  Collision and damage are the object's (its card's box, 100 HP).

## Light and shade

Lit by the engine alone the plants read as flat cut-outs pasted on the land (a user's report; measured with the
debug views: grey plants on a grey ground lit like the ground or brighter, the inside of a crown as bright as its
rim, every blade as bright as the next, nothing darker under a plant while the cars stand on their drop shadows).
The sky's light is the same everywhere and the sun's shadow map reaches only the nearest shrubs at High, none at
Low, so the occlusion a real crown gives itself has to be baked; it is procedural, so it is cheap.

- **Baked occlusion** (`PlantShading`). The full mesh's surfaces are splatted into a grid over its bounds (14
  cells along its longest side; foliage cards at 0.12 of their area, for their texels' coverage and leaf angles,
  stems, pads and blades at 0.2), smoothed, as an extinction per metre. From each vertex 32 rays (a Fibonacci
  sphere, those in its normal's hemisphere, cosine-weighted) are marched through it; a ray that reaches the ground
  inside the plant ends on shaded ground (0.45 of the light). The mean transmittance, at least 0.2, is the vertex's
  occlusion. The lighter tiers take the full mesh's values where their vertices lie (the full mesh's averaged into the
  grid's cells, foliage and the rest apart, trilinear), then their foliage's values are curved (a power) to the full
  mesh's area-weighted mean, so a plant does not brighten when its tier changes. A dense saltbush or sand sage comes
  out 0.2 deep inside and 0.8 on its rim, an open creosote 0.3 / 0.8, a yucca's head 0.7 / 1.
- **The shader** uses it for the sky's light (`AO`) and 60% of the sun's (`AO_LIGHT_AFFECT`, as the shadow map is
  rarely there to do it), and on the foliage's backlight.
- **The shade on the ground** (`vegetation_shade.gdshader`). Every shrub, and every ground-layer plant reaching 0.28 m
  or more (the rosettes, tussocks, tumbleweeds; not the grass tufts), gets a quad 1.3 times its reach (the
  horizontal distance four in five of its vertices lie within), laid on the ground's slope there, in one MultiMesh a
  chunk. It multiplies the ground: 1 − 0.85 × the share of the sky's light the plant takes from its foot (the
  occlusion grid seen from the ground at its middle and four points round it: saltbush 0.94, gray oak 0.72, creosote
  0.29, ocotillo 0.24, soaptree 0.14), easing out past the crown's rim, and 0.3 darker at its very foot. The quad is
  pulled toward the camera (0.12 m and 0.4% of the distance) so the drawn ground, whose level of detail can lie a
  little above the plant's base, never hides it; it fades out before the terrain's coarser levels move the ground.
- **The ground's light.** The plants are tinted 22% toward the ground's colour (12% before), which carries the
  mission's light (T16's red dawn) that the painted atlas does not, and on a dark ground they are darker:
  √(ground luminance / 0.085), 0.78 to 1 (T15-T17's red earth, luminance 0.047: 0.78; the South Plains and the
  Trans-Pecos, 0.09: 1).

`I76_VEG_DEBUG=1` with `I76_TERRAIN_DEBUG=2` draws plants and ground grey, the light alone; `I76_VEG_DEBUG=2` draws
the occlusion.

## The sward

Tufts on bare dirt read as dots however good the tufts are; a shortgrass prairie looks covered. So the ground between
the plants takes the dry grass's colour where the grasses grow:

- **The map** (`SwardMap`): a byte a 4 m texel over the bounds of the map's tiles (T02: 960 × 2560), the placer's
  `Sward`: the ground layer's cover there (the patches, the riparian strip, the grasses' clumps, open or broken ground,
  the slope) against the biome's, times the grasses' share of the species' weights there, times the biome's `Sward`
  (South Plains 0.8, Davis Mountains and the prairie 0.85, the South Plains gone to weeds 0.55, sand country 0.45,
  Pecos, the desert mix and the suburb 0.3, Trans-Pecos 0.25, roadside weeds 0.15, the dream and the salt flat none);
  nothing on steep ground or under an object (the roads' layer paints over it). It does not depend on the setting's
  density: Low's ground is as grassy as High's. The broad part (`SwardBroad`: everything but the clumps, the open
  ground, the slope and the objects) is read every 16 m and interpolated, the rest at every texel: about 0.2 s on the
  pool for T02 (24 threads), made while the meshes are; the loading card waits up to 1.5 s for it, after which a slow
  machine's eases in.
- **The shader** (`terrain_modern.gdshaderinc`, natural grounds in the modern look only; the globals `i76_sward`,
  `i76_sward_rect` and `i76_sward_tint`, whose strength is 0 with no plants, at Off and in the 1997 look): a gentle
  shift of the ground toward dry grass by the map (smooth over its 4 m texels), varied by a broad 23 m octave (±30%)
  and nothing finer. A closed sward moves the ground 0.3 of the way (`sward_cover`) to a dry grass that keeps 0.6 of
  the ground's own lightness (`sward_keep_lum`), straw where it is thin and greener where it is dense, so the mission's
  ground colour stays dominant (T15-T17's red earth stays red, a little browner where the grass is thick). A first
  version hid 0.62 of the ground in thresholded half-metre patches: it read as camouflage, red islands in tan, and
  changed the maps' colours. Its colour is the plants' dry grass under their light (the 22% pull toward the ground's
  colour, darker on a dark ground, the ground's saturation).

## Placement

`VegetationPlacer`, deterministic: the mission's name seeds it, and each layer's grid cell (shrubs 4 m, grasses and
forbs 2 m; 1.5 m in the grasslands) holds at most one plant, decided from its own coordinates, so any chunk built in
any order grows the same plants. Per cell, at a jittered point:

1. no terrain there, or the drawn surface moved sideways there (relief off the drivable cells, more than 0.35 m),
   or steeper than 1.2 rise over run: nothing;
2. the cover: the biome's upland cover blended to its low-ground cover by the low-ground weight (only where the
   region has a low-ground strip: 1 on the river bottom, 0 more than `RiparianBand` above it, the mission's low
   ground being the 3rd percentile of the drivable samples: T11's Pecos bed at 0.2 m), times patch noise (bare
   stretches and denser stands at ~55 m), times thicket noise in the riparian strip, 0.7 off the drivable cells,
   less on slopes; the roll is against the cover times the setting's density (and the region's factor);
3. a species by weight: each species' weight times its riparian affinity (−1 uplands only … 1 river only), its own
   patch noise (`Clump`), its slope limit, its liking for rocky ground (`Rocky`, −1 flats … 1 slopes: the ground's
   rockiness is its slope from 0.12 to 0.5 rise over run, or 0.5 off the drivable cells; the weight goes from
   1 − Rocky on the flat to 1 + Rocky on rock) and for the disturbed ground by roads (`Roadside`: within 1.5–8 m of a
   road's edge the weight is up to 1 + 2 × Roadside, elsewhere 1 − Roadside);
   the grasses' own clumps (`TuftClump`, 10–12 m, in the grasslands, the sand country and the Pecos) multiply the ground
   layer's cover like the patches (by up to 0.6 × 1.25 either way), and a grass's tuft is 0.75 to 1.25 times bigger
   from the thin ground to the dense, so a sward has denser patches of bigger tufts and thinner ground between;
   a species with a `Motte` fills the cells where its own patch noise is densest (0.62–0.85 of it): the chance a
   cell grows a plant rises there by Motte toward 1, and that share of the cells goes to it (shinnery thickets,
   mesquite mottes, lechuguilla colonies); the split is drawn apart from the roll, so Low's plants are High's, the
   same species;
4. the masks, with the plant's radius plus a clearance (shrubs / ground layer): roads (every road quad, 2.5 / 0.8 m
   past the edge), objects (each part of a placed model standing on the ground, as an oriented box, 2.5 / 0.8 m; a
   bridge's deck over a riverbed is not on the ground, so the river grows under it), the script's driving lines
   (2.5 m plus 1.5 / 0.3 m) and where cars start (6 m discs);
5. the base on the drawn ground: the lower of the surface interpolated on the 1.25 m and 2.5 m level-of-detail grids
   (split as the meshes are split), sunk a few centimetres, more on a slope.

Rough ground. The original studs its terrain surface id 5 ("rough ground": big mapped patches, about 20% of S02, 7.7% of M06, 3.5% of T05, traces in S01, M09 and S03; none in the Nitro Pack) with the big `NG3_` plants,
yuccas and agaves. The modern look reads the same surface id (`IVegTerrain.Rough`, the
sample nearest the point, bits 13-15 as `TerrainGround.SurfaceId`; visual only, nothing the simulation reads) and
shifts the mix there: the shrub cover is `RoughShrubs` (3.2) times a clump noise (`RoughClump`, 18 m, 0.1 to 1.9, mean
1) so the thickets come in groups with thinner ground between, the weight of each species with a `Rough` factor in
its `BiomeSpecies` row is multiplied by it (soapweed yucca 3.5, cholla 5, soaptree and Torrey yuccas and agave 3,
sotol 2.5: only the species the region already grows), and the ground layer's cover and the sward are 0.7 times
what they are elsewhere (`RoughGround`). Every other surface is untouched.

Road surfaces. "Plants follow how the ground drives": the original grows clutter only on surfaces 0, 1 and 5, and the
road surfaces (2 asphalt, 3 dirt road, 4 plank road, 6 parking lot, 7 dirt intersection) are smooth and harmless in the
simulation, so they grow nothing here either: `IVegTerrain.RoadSurface` (the same sample read as `Rough`,
`VegSurface.IsRoad`) bars both layers in `VegetationPlacer.PlaceCell` and zeroes the sward in `SwardFine`, so the ground
is bare dirt there (T14's id-3 flat round its crossroads, dotted with id-1 islands that keep their bushes). Two
exceptions keep the biome as it was: the roadside strip, within `RoadsideStrip` (5 m) of a mask road's edge (just past
the 2.5 m shrub clearance, where the snakeweed, tumbleweeds, prickly poppy and roadside grasses grow), and the
low-ground strip (riparian weight above 0: T11's Pecos bed is id 4). Nothing else changes: the roll for a cell on
surfaces 0, 1 and 5 is the same, so those plants are bit for bit as before.

T11's biome (`Biomes.Pecos`): shrubs creosote 1.0, saltbush .35, mesquite .3, cholla .2, prickly pear .24,
soaptree .1, soapweed .13 on the uplands, salt cedar 1.2 and willow .55 on the river bottom (mesquite and saltbush
in both); ground layer grama 1.0, tobosa .6, snakeweed .3, tumbleweed .1, sacaton 1.2 by the river. Cover per cell:
shrubs .26 uplands / .65 river bottom, ground layer .45 / .6. The other biomes' weights are in `Biomes`; a new
mission gets a region by a row in its table.

## Drawing

The map is cut into chunks (160 m for shrubs, 80 m for the ground layer). Chunks within a layer's reach plus 40 m
are placed on worker threads (up to half the cores), nearest first, and attached a few a frame; chunks beyond the
reach plus 80 m are dropped. At the mission start the chunks round the first view are placed on every core behind
the loading card. Each species variant in a chunk is a MultiMesh (Transform3D plus custom data: stiffness, sway
phase, height, colour variation; explicit bounds) per detail tier in use: a chunk lying within one tier's band
draws all its plants at it, one straddling a band splits them plant by plant by their distance, redone when the
eye has moved 4 m. Shrub chunks nearer than the shadow range (High) cast sun shadows from their full and middle
meshes. Tier changes cross-fade (`TierBands`): about each boundary (the middle tier's start, the far tier's) lies a band of
+-max(1.5 m, 20% of the boundary); in it a plant is in both tiers' buffers, and the shader (`tier_in`/`tier_out`, one material
per layer and tier, from the plant's ground distance) draws the incoming tier where the 4x4 dither passes and the outgoing one
where it does not, so coverage stays constant. A split chunk sorts plants with 2.5 m of slack for the eye's movement. The far
tier keeps a grass variant's small side tufts (one card each). Low's full-mesh range is 9 m (was 7), High's 12 m (was 11).
Measured cost: not profiled (stopped on request).

The shader sways each vertex by its weight squared, the plant's height and the stiffness (cacti and yuccas barely),
in a gust front crossing the land; foliage flutters. Plants fade out by a 4 × 4 ordered dither against the main
camera's distance (set every frame, as the shadow pass's camera is the light's), so they thin out pixel by pixel
and never pop. Colours are tinted toward the mission's ground colour and darker on a dark ground ([Light and shade](#light-and-shade)), and the bases take its dust. The look
switch hides the node; a mission started in the 1997 look builds none of it until the modern look is first shown.

## Performance

T11, an integrated GPU, 1920×1080, the frame profiler (`tools/frame-profile.sh`, medians of 3 runs):

GPU frame time (ms), with the triangles drawn (thousands, every pass) and draw calls:

| View | Off | Low | High |
|---|---|---|---|
| the start, chase view (a mesa top, few plants) | 15.0 (1656 k, 1932) | 15.1 (1904 k, 2194) | 15.8 (2450 k, 2427) |
| chase view on Highway 380 east of the river (creosote flats) | 13.7 (1801 k, 1163) | 14.5 (2101 k, 1423) | 15.1 (3516 k, 1969) |
| over the river bottom from 14 m (the densest: salt cedar thickets) | 15.8 (1231 k, 901) | 17.7 (1775 k, 1268) | 19.9 (2715 k, 1579) |

The main thread's render work grows by 0.0–0.7 ms (the draw calls); placing chunks runs on worker threads. Most of
the cost is the alpha-tested foliage in the depth pre-pass and the opaque pass (4x MSAA); the three detail tiers
keep the triangle count down (a plant beyond 30 m on High, 20 m on Low, is a third to a sixth of its full mesh).

The regions, measured the same way (a free camera 3 m over open ground near each mission's start after its intro,
`I76_SHOT_FPS=30`, `I76_FRAME_PROFILE_SKIP=850`; T11 re-measured alongside, at its creosote flats):

| View | Off | Low | High |
|---|---|---|---|
| T11 (Pecos) flats, 4092,49097 east | 12.2 (1454 k, 776) | 12.9 (1621 k, 978) | 14.0 (2460 k, 1366) |
| T02 (South Plains), 9047,43797 east | 11.7 (1770 k, 679) | 12.0 (1879 k, 886) | 12.8 (2308 k, 1144) |
| T13 (Trans-Pecos), 5882,44842 east | 11.6 (1202 k, 1039) | 12.6 (1530 k, 1306) | 13.7 (2453 k, 1551) |
| T16 (Davis Mountains), 1652,50137 north | 10.1 (808 k, 232) | 10.8 (1084 k, 485) | 12.1 (1817 k, 746) |

Low costs 0.3 to 1.0 ms, High 1.1 to 2.1 ms, as on T11 (0.7 / 1.8 ms here); the denser prairie grass of the South
Plains and the Davis Mountains is mostly the ground layer's cards, the Trans-Pecos the heaviest (ocotillo and yuccas).

The occlusion and the shade on the ground ([Light and shade](#light-and-shade)) cost nothing measurable: the same
views (a free camera 3 m up, `I76_NO_SCRIPT_CAM=1`, 400 frames, the profile from frame 250), before and after in one
alternating run, medians of 3, GPU ms over Off:

| View | Off | Low before / after | High before / after |
|---|---|---|---|
| T02 | 7.50 | +0.60 / +0.35 | +1.06 / +1.04 |
| T11 | 9.11 | +2.11 / +1.62 | +2.14 / +1.42 |
| T13 | 8.49 | +0.98 / +1.00 | +2.30 / +2.40 |
| T16 | 8.15 | +0.64 / +0.68 | +1.79 / +1.92 |

The shade quads are 5–10 more draw calls (one MultiMesh a chunk) and 0.01–0.08 ms in the transparent pass; the
rest is noise (T11's runs spread by a millisecond, the GPU shared with other work).

The bunchgrasses and the sward ([Bunchgrasses](#bunchgrasses), [The sward](#the-sward)), the same views from 3 m up
(`--cam`, pitch −8°, `I76_NO_SCRIPT_CAM=1`, 400 frames, the profile from frame 250, 1920×1080), before and after in one
alternating run, medians of 3, GPU ms over Off, and the sward's share (High against High with `I76_SWARD=0`):

| View | Off | Low before / after | High before / after | the sward |
|---|---|---|---|---|
| T02 | 7.13 | +0.13 / +0.41 | +0.72 / +1.26 | 0.20 |
| T11 | 7.13 | +0.07 / +0.58 | +1.01 / +1.47 | 0.05 |
| T13 | 7.30 | +0.64 / +1.16 | +1.78 / +2.35 | 0.21 |
| T16 | 6.51 | +0.45 / +1.07 | +1.64 / +2.25 | 0.13 |

Low now costs 0.4 to 1.2 ms, High 1.3 to 2.4 ms: 0.3–0.6 ms more, nearly all of it the opaque pass. The sward is
0.05–0.2 ms in the first, patchier version measured here (the gentle one reads one broad octave, no more); the rest is the grass:
the full tufts are 2–3 times the vertices they were (hence nearer: 11 m on High, 7 m on Low, from 14 and 9) and the
clusters' cards cover more of the screen (two cards for a main tuft instead of three saved nothing measurable). The
shade quads and draw calls are unchanged.

Making the meshes (three tiers of a biome's 28 to 40 variants, with their occlusion baked: a job per variant), the
atlas and the masks takes 0.15–0.3 s (the sward map alongside, about 0.2 s on T02), placing and attaching
the first view's chunks 0.04–0.35 s (Low 2600–3300 plants, High 7700–9700), both behind the loading card; a
mission without a biome, Off, or a 1997 start spends nothing.

## Cars and plants

Plants have no collision, so two shader-side rules keep a car and the plants apart (`World/CabClip.cs`, one node):

- **Bend.** The cars' positions (the player's first, then others within 120 m, 16 at most) go to the shader as the
  float texture `i76_cars` (x, y, z, radius 2.6 m) and `i76_car_count`. In the vertex shader a plant whose root is within
  1.4 m of a car is fully parted, and fades out by 4.4 m: its crown is pushed away from the car by 0.7 of its height and
  laid down by 0.6, weighted by the sway weight (the root stays put). Stiffness scales it to 0.15 (cacti, yucca). It is
  distance-based, so the plant springs back as the car leaves.
- **Cab clip.** While the cockpit is the current camera (not in a cut-scene, not wrecked) the interior model's bounds, cut
  off 0.85 m ahead of the eye (about the windscreen's foot, so the hood is outside it), go to the shaders as an oriented
  box (`i76_cab_inv`, `i76_cab_half`; zero half-extents are off). `cab_clip.gdshaderinc`'s `CAB_CLIP` discards fragments
  inside it, thinning out over a 0.3 m margin by interleaved gradient noise. The plants and the effect shaders that can
  drift through the car (damage smoke in both looks, wreck smoke, wreck and fire-drop flames, napalm, cluster fire, flipbook
  explosions, rotor discs) call it; the mirror's camera sits in the cab and clips alike. Ground decals, the car's own
  lamps and muzzle flashes, tracers and the shadows are left alone.
  `I76_CAB_CLIP=0` turns both rules off for before and after captures.

## Tumbleweeds

A few dead Russian thistles blow across the open flats (`TumbleweedField`, pure; `TumbleweedNode`, a child of the plants'
node, so Vegetation Off and the 1997 look hide it with them, at once on F12, and it carries on where it was on switching
back). Visual only, like the plants: nothing the simulation or a network game reads, each player sees their own, seeded
from the mission so a capture repeats.

**Where and how many.** Each biome has a rate (`Biome.Tumbleweeds`, 0..1 of the most: 4 on Low, 7 on High, rounded),
times the region's density (A01: half):

| Biome | Rate | Low / High |
|---|---|---|
| South Plains gone to weeds (M06) | 1 | 4 / 7 |
| Roadside weeds (M03, the airport) | 0.9 | 4 / 6 |
| South Plains (T01-T03, T05-T09, the scenarios, the desert arenas by ground) | 0.6 | 2 / 4 |
| Sand country (T10, S06) | 0.5 | 2 / 4 |
| Pecos (T11, T12, S03, its arenas; not on T11's river bottom) | 0.4 | 2 / 3 |
| Desert mix (other desert maps) | 0.3 | 1 / 2 |
| Suburb (M04) | 0.25 | 1 / 2 |
| Trans-Pecos, Davis Mountains, the dream, the salt flat, the prairie; none on snow and asphalt | 0 | none |

They roll within 150 m of the camera on Low, 180 m on High, fading out by the plants' dither over the last 45 m
(and a fresh one fades in over a second), so at most a handful are in view, mostly none or one.

**The wind** (`Wind`): one direction for every mission, the one the plants sway toward (`wind_dir`, Godot's (0.8, 0.6),
the game's (0.8, −0.6)) and the damage smoke drifts in. Its speed is the plants' gust front (a wave crossing the land
downwind at about 13 m/s, where the shader's is, on a clock of its own) squared, times slow lulls that fall nearly calm: up to 7.5 m/s.

**Rolling.** Each is a ball 0.5-1 m across. The wind drags it by the square of its speed through the air (about 0.5 per
metre, its own share ±20%); on the ground the slope pulls it back by 0.6 g times the rise (a light, hollow ball: about
a hollow sphere's 2/3), rolling resistance (0.12 g) slows it and it needs a push of 0.22 g to start from rest, so it
stops in a lull and goes again in a gust: 2-5 m/s in the gusts, 2 on average, still a quarter of the time. It follows the drawn ground (the plants'
height), flies off a crest where the ground falls away faster than gravity can bring it down, and hops off the small
bumps (a chance per metre rolled, more the faster it goes, now and then a big one): 13% of the time in the air, hops
up to 0.8 m. In the air the wind has it all and it falls with a little drag; it lands with a soft bounce (a third of
the impact back, 15% of its speed lost to the ground) if it hits hard. On the ground it spins as it rolls, ω = n × v / r
(the point touching the ground is still); in the air its spin decays slowly.

**Obstacles.** At an object's footprint (the plants' masks: buildings, walls, props on the ground) it turns aside to the
nearest clear heading within 90 degrees, slowed, or stops against it; a shrub in its path (the placer's own shrubs,
cell by cell) catches it six times in ten for 1-4.5 s, rocking in the wind, until a gust tears it free round the side,
else it glances off round the crown with a hop. Roads it crosses. A car standing or creeping (under 3 m/s) holds it off.

**Starting and going.** A new one starts on the ring upwind of the camera (within 75 degrees of straight upwind, at
the range: out of sight) on flat enough ground (rise under 0.5) clear of the objects (and off T11's river bottom), and
rolls in; one that has rolled 10 m beyond the range or off the land goes and a new one starts upwind, so the count
holds. One lying still for 30 s (in a long lull, or against a wall) fades out and starts again. A camera cut, the
first frame and a higher level scatter them anywhere within the range, fading in.

**Cars.** A car (the cars the plants part round, `World/CabClip.cs`) moving at 3 m/s or more that sweeps within
1.9 m plus most of its radius bursts it: the main tangle is thrown up (2.5-5.5 m/s) and ahead of the car and out to
the side it was on, spinning (7-16 rad/s), with three smaller torn clumps (0.4-0.55 of its size, squashed), carried
along at 55-90% of the car's speed, slowing quickly in the air, bouncing, fading out over 1-2.4 s; a new one starts
upwind. Nothing touches the car.

**Look.** A rolling one is its own mesh (`SpeciesCatalog.RollingTumbleweed`): six cards of the tumbleweed's painted
tangle facing every way (an icosahedron's axes, turned at random), so it reads round from any side as it turns, straw
and dry, its occlusion baked in the air (no ground under it); three variants. It is drawn with the ground layer's
material (`rolling`: no sway, no parting round cars, no dust at its foot; its fade from the instance) in one MultiMesh
a variant, the pieces in them too, with a soft shade under each (the plants' shade quad, fainter and wider as it hops,
gone by 90 m); on High they cast sun shadows.

**Cost.** The motion is a little arithmetic a tumbleweed a frame on the main thread; the drawn ground's points
are kept as they are read (`SurfaceTerrain.Cached`), as the same few cells are read frame after frame. T01, 60 fps,
`I76_VEG_STATS=1`: about 0.05 ms a frame with Low's two, 0.15 ms with 16; 4 MultiMesh nodes, at most 56 instances.

## Captures

`I76_VEG_GALLERY=x,z[,id,...]` lines the mission's species (or the ones named, from any region; `all`: every one) up
from game point (x, z) along X, variants behind, for close-ups; `I76_VEG_GALLERY_STEP` spaces them (4.5 m; 1.4 for
the small plants). T16's flat at `1652,50137` with `I76_FREECAM=1657.5,10,50128.5,0,-10,50` frames four shrubs; the
free camera waits for a mission's intro to end, so give it time (`I76_SHOT_FPS=30 I76_SHOT_FRAMES=900`);
`--cam x,y,z,yaw,pitch` takes over at once. `I76_VEG_GALLERY_TIER=1` (or 2) shows the middle (far) meshes.
`I76_VEG_STATS=1` prints the placement's cell counts and the plants near the camera.
`I76_TUMBLEWEEDS=N` rolls that many tumbleweeds wherever there are plants, and `I76_TUMBLEWEED_AHEAD=D[,S]` puts the
first D metres ahead of the player's car (S to its right) as it drives off: T01 with
`--drive --chase`, `I76_TUMBLEWEED_AHEAD=12,-1`, `I76_CAP_CAM=0,2.5,8,0,-8` and `I76_SHOT_FPS=30` drives into it at
about frame 55.
