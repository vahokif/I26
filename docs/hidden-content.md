# Hidden weapons and vehicles, and Nitro's WEAPON RESTRICTIONS

What the game data holds that the shells never offer, the community's old unlock, how the Nitro Pack's
WEAPON RESTRICTIONS checklist worked, and what I26 does with all of it. Read from the shipped front-end DLLs and the Nitro executable, and checked against the data of both installs; the hidden vehicles were driven in I26.

## 1. Where the shells' lists come from

- **Weapons.** The parts catalogue is built by the engine: one entry
  per `.gdf` in the database, typed by the GDFC class (`I76.Sim.Trip.PartsDatabase`). The melee CONFIGURE CHASSIS
  pickers are that catalogue filtered by a **hard-coded list of file-name prefixes** in the shell, compared with
  `_strnicmp` on the entry's file stem (`+0x3b`):
  - the original front end's weapons (types 6, 7, 9, 10, 11) drop `gh45`, `gtktank`, `gtptank`, `tthowitz`
    always, and `gchades`, `tchades`, `gscherub`, `tscherub`, `ggchem`, `gs4get`, `gsball`, `tsball`, `tm125mg`,
    `tm125lmg`, `gfpyro`, `tfpyro` unless the variant's name begins `natk`. Droppers drop `gcal*`, `gsmo*`,
    `gceracer`.
  - the Nitro front end: the same shape with a shorter list. Always out: `gh45`, `gtktank`, `gtptank`,
    `tthowitz`; out unless `natk`: `gchades`, `tchades`, `gscherub`, `tscherub`, `tm125mg`, `tm125lmg`, `gfpyro`,
    `tfpyro`. Droppers drop only `gceracer`. Nitro's own weapons (`ggchem`, `gs4get`, `gsball`, `tsball`,
    `gcaltrop`) are offered.
  - The trip garage has no filter: its inventory is what salvage gives, and the salvage tier tables
    (the same 39 names in both games) are the only gate. They hold
    HADES, Cherub, Pyro-Tomic (and their turrets), EZ Kill and Car-E-Racer at the top tiers (the trip's last
    missions); never the handgun, the tank cannons, the howitzer, or any Nitro-only weapon.
- **Vehicles.** The base game's MAKE / MODEL list is a **hard-coded table** in the original front end (23 models:
  `I76.Game.Shell.MeleeData`). Nitro's comes from a **data file**, `nitcar.def` (37 rows, the shell writes its
  built-in table there on first run; `I76.Formats.Shell.CarTable`). Any other car is reachable only
  through the Phaedra Rattler's variant-name secrets, applied when the variant is saved:

  | variant name | base (the original front end) | Nitro (the Nitro front end) |
  |---|---|---|
  | `knat…` / `natk…` | `vstank.vdf`, `tank1.vtf`, tank tracks, all 8 armour/chassis 1500 (`knat`) or 6000 (`natk`) | the same |
  | `retpocileh…` | `vuchuey.vdf` (a file that ships nowhere), `huey2.vtf`, front 3000 | `vuhuey.vdf`, `huey2.vtf`, front 3000 |
  | `hotair…` | — | `vuballon.vdf`, `balloon1.vtf`, front 3000 |
  | `thetruthishere…` | — | `vxufo.vdf`, `ufo1.vtf`, front 3000 |

  A `natk` variant also opens the second half of the weapon ban list above (the "super tank" of the cheat sites).

## 2. What is in the data and not offered

### Weapons (GDFC display name, class / tier)

| file | name | class/tier | base | Nitro | state |
|---|---|---|---|---|---|
| `gchades` / `tchades` | HADES Cannon / Turret | 2/6, 2/106 | natk | natk | **complete**: model, muzzle flash, `whades.wav`, plates `3hades_can_on`/`3hades_trt_on`, trip salvage tier 7 |
| `gscherub` / `tscherub` | Cherub Msl / Trt | 4/3, 4/103 | natk | natk | **complete** (the mail truck's missile in T17) |
| `gfpyro` / `tfpyro` | Pyro-Tomic / Pyro-Turret | 5/3, 5/103 | natk | natk | **complete** |
| `gceracer` | Car-E-Racer | 6/6 | never | never | **complete** (dropper; trip salvage tier 7) |
| `gtktank` / `gtptank` | Tank Cannon / Police Tank Cannon | 2/15 | never | never | works (the tank's gun), but its cockpit plate is `3empty_on`: the dash reads EMPTY |
| `tthowitz` | Howitzer | 3/103 | never | never | **complete**: the emplacement's gun (`vxhowit1`, `gthowtzg.pak`), `whowitz.wav`, a shell (`xmslb1.xdf`) that lands and bursts; plate EMPTY. On a car's turret mount its shield sits over the roof |
| `tm125mg` / `tm125lmg` | 12.5mm MG / Light m/g "with gunner" | 2/107, 2/108 | (absent) | natk | **complete**: the sandbag emplacements' guns (`vxsndbg1/2`): a gunner figure standing at a gun on a pole (`G1AT*.geo`, `gm125g.pak`), drawn as part of the gun and turning with it (no animation of its own: the Nitro Pack has no gunner code), `wmmgun.wav` / `wlmgun.wav`, bullets; plate EMPTY. The all-weapon shell's "dudes on sticks" |
| `gh45` | 45 Caliber Handgun | 1/0 | never | never | the on-foot pistol (class 1, part type 9), placeholder fields (`mmgun.bmp`, `mmflash.geo`): not a car weapon, left out |

### Vehicles (models with `.vcf`s that no list offers)

| stem | model | base | Nitro | state |
|---|---|---|---|---|
| `vstank` | Tank (Army, Police) | hidden | hidden | **complete** (the `knat` body; drives on its tracks) |
| `vuchuey` | UH-1B Huey (3 variants) | hidden | hidden | **complete**, flies (class 9) |
| `vugcobr` | AH-1 Cobra (2) | hidden | hidden | **complete**, flies |
| `vxktrac` | Tractor "Farmer John" | hidden | hidden | **complete**, drives, unarmed |
| `vxmilk` | Moth tanker truck (3) | hidden | offered | **complete**; base's Milk Truck variant names `gsball.gdf`, which only Nitro has, and is built without it |
| `vsvan` | Skeeter's Van (2) | hidden | offered | **complete** |
| `vubalon` | Hot-air balloon | — | hidden | **complete** (`hotair`), flies, unarmed |
| `vxufo` | UFO "Martian" | — | hidden | **complete** (`thetruthishere`), flies, unarmed |
| `vuphant` | F4D Phantom II | — | hidden | **complete**: flies (helicopter class 9); stock unarmed (no HLOC), but I26 adds four hardpoints so a garage variant can be armed: a side-style gun under each wing (ids 0 left, 1 right) a belly dropper (id 2) and a belly turret mount hanging upside down under the fuselage (id 3, style 1) (`VehiclePatches`, deviations.md). A variant with guns on them loads unarmed in the original |
| `vdillo` | Armadillo | hidden | hidden | **complete** as a joke car: the scenery critter, legs as wheels (`wdilo_1a.wdf`), 337 kg, about a metre long, no hardpoints and no cockpit model. Drives; unarmed |
| `vxhowit`, `vxsndbg`, `*turr` | Howitzer, sandbag MGs, pole turrets | hidden | hidden | stationary emplacements (class 8, no wheels or engine): left out |
| `vppt*`, `vppa`, `vgoonf`, `vcgcourf`, `vstankf`, `vhelif`, `t*`/`s*`/`p*` | trip, scenario and mission cars | — | — | variants of the models above, not models: left out |

Each "complete" vehicle was driven in I26 (`--drive X.vcf` on M01 and N01): models, paint, engine loops and the
flying classes all work. The Armadillo has no interior model, so I26 starts it in the chase
view and F1 / V (cockpit, binoculars) fall back to the chase view; its legs spin as wheels.

## 3. The old "file swap"

The trick the tester remembers was an **"all weapon" `NITSHELL.DLL`**: a patched Nitro shell, dropped over the
original, that listed the banned weapons in CONFIGURE CHASSIS. Lightfoot's *Unofficial Nitro Patch IV* (pre-release
1 February 2018) shipped it and pulled it the day before release: *"Looks like there may be a compatibility issue
with the all weapon NITSHELL.DLL and on-line games. I've updated the patch and removed it from the manifest. Please
restore your original NITSHELL.DLL if you're having problems."* (forums.interstate76.com, topic 1442, 27 Feb 2018;
the archived Patch IV zip has no shell, Patch III's `Nitshell.dll` differs from GOG's only by a 10-byte stack fix). No copy of the patched DLL was found. Since the only filter is the prefix list
(section 1), that is all it can have changed; the weapons themselves were always in `nitro.zfs`.

## 4. Nitro's WEAPON RESTRICTIONS

### The checklist (the Nitro front end)

- **Button**: host form hotspot 5 (31..193, 310..326, not on the join forms). Case 5 of the form
  handler opens a modal: bank 30 sprite 43 (573x430, "WEAPONS RESTRICTIONS / SELECT WEAPONS NOT
  ALLOWED IN GAME", the labels printed on the art) at (34, 25); hotspots `{2: 39..387, 66..382}` the boxes,
  `{8: 400..471, 400..416}` DONE, `{3: 479..550, 400..416}` CANCEL. Edits go to a copy; DONE copies it
  back and rebuilds the list, CANCEL drops it.
- **Rows**: 43 entries of 0x38 bytes: `{group, class, tier, checked, column, row, name[32]}`.
  A box is drawn at popup + (`columnX[column]`, `rowY[row]`): columns = 39, 64, 93, 321, 346, 375
  (header, STD, TRT on each half), rows = 66, 98, 114 … (16 apart, with gaps at 178→210 and 274→290).
  A check is the form font's `x` at (x+1, y−2). The rows: All Weapons (class −1); per group a header
  (tier −1: All Slug Throwers 2, All Arc Launchers 3, All Missile Launchers 4, All Flame Throwers 5, All Droppers 6)
  and one row per offered weapon and turret, keyed by the GDF **(class, tier)**. Ball O'Flame sits under
  flamethrowers but is class 4 (a missile).
- **Clicks**: a box toggles. A header carries its state to the rows after it in its group; a row
  unchecked under a checked header clears the header; any row unchecked clears All Weapons. All Weapons itself does
  not tick the others.
- **The list**: All Weapons checked → count −1. Else, in table order, a checked header adds
  `(class, −1)` and skips the rest of that class; a checked row adds `(class, tier)`. The block (0x198 bytes:
  `strict = 1`, count, up to 50 pairs) is kept in a global.

### How it reaches the game

The host publishes the block as its anet **player data key 3** (key 1 its car, 2 the game,
4 its car's CRC, 5 the session) and hands it to the engine (the engine stores the count and the pairs). A joiner reads key 3 with key 5, shows it on
the join form's session panel ("No Restrictions", "All Weapons", or the names, four rows that scroll), passes it to its own engine, and is copied into the launch block.

### How the Nitro Pack enforces it

The car check parses a `.vcf` with its own chunk table. With the
strict flag set (always, from the host's block) it marks:

| bits | check | handler |
|---|---|---|
| 0..6 | a weapon on hardpoint *n* whose `.gdf` (class, tier) matches a restriction (tier −1 matches the class; count −1 matches every weapon) | WEPN |
| 0x80..0x200 | the same special twice | SPEC |
| 0x400, 0x800 | armour + chassis not exactly the body class's budget, or a zone under 1/32 of it | VCFC |
| 0x1000 | tank tracks or balloon/critter wheels on a car not made for them | VCFC |
| 0x2000 | `gtktank`, `gtptank` or `tthowitz` | WEPN |
| 0x4000 | a second turret | WEPN |
| 0x8000 | a dropper off a dropper mount, a gun on one, a turret off a turret mount | WEPN |

It also folds every `.gdf`, the `.vdf`, `.vtf`, wheels and `compnent.cdf` it read into a CRC. The check runs twice:

1. **On the joiner's own machine, before it joins**: a refusal closes the connection and shows
   the host's restriction list (when only weapon bits are set) or the "illegal vehicle" notice
   (bank 30 sprite 33). The host runs it on its own car too.
2. **On every machine when a player's car arrives**: the car's `.vcf` comes as player data key 0
   and is written to `NVCL\nvclN.vcf`; the receiver checks it against its own restriction list and its own files
   and compares the CRC with the one the player published (key 4). Either failing prints "*** %s has tried to join
   with a hacked vehicle!" and calls `dpDestroyPlayer`. The `-hacks` or `-old` command-line switch
   turns this check off.

### Why hidden weapons slipped through

- **The checklist cannot name them.** Its rows are the stock pickers' (class, tier) pairs; HADES (2/6, 2/106),
  Cherub (4/3, 4/103), Pyro-Tomic (5/3, 5/103), the gunner MGs (2/107, 2/108) and Car-E-Racer (6/6) have none.
  A host who ticked every row still let them in. Only a group header (whole class) or All Weapons covered them; the
  handgun (class 1) only All Weapons.
- **Every check runs on a player's machine, against that machine's files.** The pre-join check is in the joiner's
  own shell and engine; the arrival check is in each peer's engine (a peer's kick needs the host's anet rights to
  stick) and is off for a host started with `-hacks`/`-old`. A patched shell or exe skips them, and the CRC only
  catches data that differs between machines. There is no authoritative check anywhere; that, not the checklist,
  is how "all weapons restricted" games could still see them.
- The tank cannons and the howitzer were always refused online (0x2000), restricted or not; the `natk`/`knat`
  bodies too (their armour is off the class budget).

## 5. What I26 does

- **WEAPON RESTRICTIONS** on the Nitro host form: the original's popup and rows, read from the install's
  the Nitro front end at run time (`RestrictionTable`; no table is copied into the repository), with the original's click
  rules and list (`RestrictionChecklist`). The port adds boxes in the art's empty lines for the complete hidden weapons,
  named from their `.gdf` in the form's font (`HiddenContent.ChecklistRows`): Cherub and Pyro-Tomic (STD, TRT) under
  missiles and flamethrowers, HADES (STD, TRT), the Tank Cannon (both tank guns share its class and tier), the
  Car-E-Racer and the Howitzer (TRT, under arc launchers' group) at the foot of the right half. That fills the art's empty
  lines: the gunner MGs have no box of their own (All Slug Throwers and All Weapons cover them). A group's box ticks them with its own. The list is the original's: All
  Weapons, `(class, −1)`, or `(class, tier)` pairs (`WeaponRestrictions`), kept for the shell's run.
- **Enforced by the server**, the only authority: every joiner's car (an install car asked by name, the car standing in
  for one the server lacks, or a player's own variant after the garage check) is checked against the restrictions
  before the seat is given, by the GDF (class, tier) of the server's own files. A restricted weapon **refuses the join**
  with the weapons named ("Restricted weapons: HADES Cannon"), shown as "Could not join: …" on the joiner's form; nothing
  is stripped. `i26-server --restrict all|slugs|mortars|missiles|flames|droppers|<gdf>|<class>:<tier>,...` and
  `--host-listen --restrict` do the same without the form.
- **Joiners** see the restrictions on the join form's game popup ("Restricted: …", by the checklist's names), and JOIN
  checks their car first and says what it must lose, without connecting; the server checks anyway. The list travels in
  the session answer (protocol 12, appended after the locked flag).
- **Hidden Weapons & Cars** (Play Options' first line and a row on both host forms, Off by default): the complete hidden weapons join the CONFIGURE
  CHASSIS pickers (where the all-weapon shell put them) and the complete hidden models join MAKE / MODEL after the list's
  own (no art of their own; the A.I. drivers never pick them), the Armadillo among them (no weapon lists to fill, no cockpit). In a network game the host's setting decides
  (`i26-server --hidden`; a host form sends its own): with it off the server refuses a car of a model the list does not
  offer, or with a hidden weapon, saying so and what to change ("the Tank is a hidden car and this game has Hidden Weapons
  & Cars off (the host's setting when it started the game): pick a stock car, or the host turns Hidden Wpns & Cars on in
  the host form and hosts again"). An internet game's listing carries the setting (docs/multiplayer.md, the user bytes). The
  incomplete ones (handgun, emplacements, mission cars) are refused either way.
- **Fixed on the way**: I26 used the base game's ban list on Nitro too, hiding Nitro's Chemical Mortar, 4Get-U-Not,
  Ball O'Flame and Caltrops from every car not named `natk`; and the Nitro secrets `hotair` and `thetruthishere` were
  missing. Both now follow the Nitro front end.

Not reproduced: the original's other strict-mode checks (armour budget, double turret, mounts, duplicate specials) are
covered by the server's own garage check of a player's variant (`CarConfigCheck`). The `natk`/`knat`/`retpocileh`/`hotair`/`thetruthishere` bodies, which the original kept off line by their armour, are accepted when the host allows hidden content (a deviation: testers wanted tank and helicopter games), and refused saying so when not; the flag the original raises on an empty
("null") mount under All Weapons (a stale class) is not kept.
