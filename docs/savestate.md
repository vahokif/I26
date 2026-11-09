# Save states: snapshots of the whole simulation

A running mission can be saved to bytes and restored exactly: the restored session, fed the same inputs,
goes on tick for tick as the original would have. The first users are mission checkpoints and quick saves;
the code is in `src/I76.Sim/Save/`.

## The API

```csharp
byte[] snap = session.Snapshot(host: optional bytes of the game's own);
SnapshotHeader h = SnapshotHeader.Read(snap);        // what to build: h.Build, h.Rate, h.Ticks, h.Host
// build the mission again, as a mission start does (MissionSetup.Build, the SimSession with the same
// SimRate and options, its Build set, the player's driver, a melee's Start), then:
RestoreResult r = fresh.Restore(snap);                // r.Created, r.Dropped: cars to add or take away
```

- `SimSession.Build` (`SessionBuild`) is what the session was built from: the mission's file stem, the
  install's identity (`SessionBuild.InstallIdentity`: the profile, the archive's size, the mission file's and
  its terrain's sizes and CRCs), the requested car (a trip's, a scenario's, a melee's player car), whether the
  script runs, a melee's roster and rules, a co-op game's seats (their cars) and respawn delay. Whoever builds a session sets it (`Main.SpawnCar`, `MeleeHost`).
  The snapshot carries it; `Restore` refuses a session built otherwise.
- `Restore` throws `SnapshotMismatchException` for another mission, install, car, roster, rules, tick rate
  or format version, or a rebuilt mission whose cars are made up differently (weapons, damage parts), and
  `SnapshotFormatException` for bytes that are not a snapshot or are cut short. It checks the header before
  touching anything; a failure after that (a corrupted body) leaves the session unusable: build it again.
- Snapshot between ticks (not from `FrameStarting` or `FrameDone`, which run inside one).
- The rebuild may use any rand seed: the rand stream is restored last, so nothing the build drew matters.
- `StateDump.Of(session)` is a canonical text dump of the whole state, by reflection (below).
- `SimSession.StateHash()` is a 64-bit FNV-1a over the snapshot's body, bar the views' events.

Static data from the install (models, the mission file, the terrain, the effects, weapon definitions) is
never in a snapshot. A restore rebuilds the mission from the install and then writes the snapshot's state into
the rebuilt objects.

## What a snapshot holds

The body is a series of sections, each opened by a four-letter tag that the reader checks (a reader that
meets another tag has lost step with the writer):

| Section | What |
|---|---|
| `SESS` | the tick, the skip latch, the debug-boom counter, the id counters, the rand seed |
| `VTAB` | every car the session knows (in the world, a human's, an entity's, removed ones too), by id: its .vcf, whether it was built with the player's rules, its paint (`CarPaint.ToBytes`, empty for the factory paint; restored onto the car, a melee-built one included), its make-up (class, weapons each by its file, handguns, damage parts) |
| `ETAB` | the entities made while the mission ran (a melee's cars and players): name, instance id, type, flags, car |
| `VEHS` | each car: pose, motion, controls, flags, timers, the contact record; its parameters (which damage lowers), engine, wheels (suspension filter state included), arms; the armoury (selection, link group) and every weapon (ammo, timers, burst, trigger, heat lock, turret aim, gun pitch, hit points); armour, chassis, the pick table and every damage part (special charges and on-times); a helicopter's or an emplacement's block. Then the world's car list in order, the humans, the player |
| `DRVR` | each driver: slot, car, last input, key ramps, the latched trigger request, the car generation |
| `STAT` | each structure that is not as built: root and node hit points and deaths, gates (position, target, sliding), whether its geometry was swapped or rebuilt, its bounds |
| `WRLD` | the simulation clock, the camera term, the vehicle step's frame delta, the cars the placeholder AI still drives |
| `CMBT` | settings (difficulty, unlimited ammo, melee, network), the radars (targets, range, camera flag, the E key's timers, the per-arc picks), every round in flight (mines, Blox, oil and fire lying on the ground included), the flame streams, the events the views have not taken |
| `DMGM` | the unlimited armour and chassis options, the events the views have not taken |
| `SCRP` | the script's frame counters and getdown; the poetry decks (seed and poems dealt); the host's clock, FSM tick, end, the player-driven tick; the Ptable's per-frame globals; every entity (damage recorder, standing order, nav path, waypoints, the Ptable stack, tactics and escape state, timers, the A* search in progress, the control queue, lights, co-op's aim and killer seat); the objectives; the CB radio queue; the script camera; co-op's seats (respawn timers, deaths, who is in) and the cut-scene's formation flags; the FSM VM (data, actor table, every thread's registers, condition, pending arguments and stack, co-op's match and preferred seat; the per-human counts); a melee (players and scores, respawn and regen timers, the roster's entities and slots, the kill credits, waves, the race trackers, the CTF flags and captures, the end; a bot's skill, slot and brain: its target, route and timers) |

Cross-object references are stable ids: a car by its `NetId`, an entity by its place in the script host's
list, a structure by its `NetId`, a nav path by its place in the FSM program (or an entity's own one-point
path, or the route a bot's melee gave it, `Entity.OwnPath`, whose points the entity's own record holds, or a route that is nobody's any more, a bot's old one that the AI's nav path still walks until its next plan, by its points), a weapon by its car and slot, an A* node by its place in its search, an FSM pointer by its thread.
Dictionaries and sets are written in id order, so a snapshot of a state is always the same bytes.

Numbers are variable-length (zigzag for signed); floats and doubles are their exact bits. The body is
deflated. A snapshot is 4–28 KB (10–67 KB uncompressed) over the corpus; the largest the corpus test met is
177 KB (300 KB uncompressed), with an AI's A* search of thousands of nodes under way. On T16, a warm save
takes 0.3 ms, a restore after the mission build under 10 ms (the build itself is 20 ms with the mission's
files already read, about 0.5 s from cold).

## Versioning

The header (`SnapshotHeader`) is the magic `I76SNAP\0`, the format version, the build, the tick rate, the
near/far policy, the tick, and the host's bytes; then the body's length and the body. **Any change to what a
snapshot holds or how it is laid out bumps `SnapshotHeader.CurrentVersion`.** A snapshot of another version
is refused with a clear message; there is no migration. Snapshots are for checkpoints within a session and
quick saves, not archives. Version 13 added the cars' paint (the VTAB entries, the melee players). Version 14 added a co-op game's personal camera scenes (a camera per seat, and the seat a script thread's scene is in). Version 15 added a co-op script thread's hold on a gate it shuts behind the player (the test it waits at and since when). Version 16 added the paths whose node 0 a teleport has put on the ground, and each weapon's file to a car's make-up (the Developer panel swaps weapons in place), and a co-op game's damage factor to the build (Co-op Damage); and Co-op Scaling: the build's cap, and on each entity the original it is a copy of and whether its copies were made; and Co-op Rolling Start to the build.

## The completeness guard

Every class of the simulation's state declares, next to its `Save`/`Load`, what the snapshot does with each
of its instance fields (`SaveAudit`):

- **Saved**: written and read back.
- **Structural**: rebuilt by the mission build from the install and the `SessionBuild` (a weapon's
  definition, a car's box, a structure's name, the wiring between objects).
- **Derived**: recomputed on restore from what is saved (a structure's polygons after its nodes died, the
  poetry decks dealt again from their seed, the script host's car-to-entity map, the bridge tree).
- **Transient**: of no consequence to what the simulation does next (per-tick outputs cleared at the next
  tick, the game's hooks, logging).

Every non-saved field carries its reason. The tests that hold it together (`tests/I76.Sim.Tests/Snapshot*`):

- `SnapshotGuardTests.TheAuditNamesEveryFieldOfEveryAuditedClass`: every field of every audited class is in
  the audit, and the audit names only real fields. **A field added to a state class fails this test** until
  it is saved or marked.
- `EveryClassTheStateReachesIsAudited` / `TheLiveStateHoldsOnlyAuditedClasses`: every class reachable from the
  session through saved fields (statically, and on a live session) has an audit. A new state class fails
  these.
- `EverySavedFieldReachesTheSnapshot`: on live sessions (a scripted mission with helicopters and wreckage, a
  melee in a fight, a network melee, missions with gun emplacements, the Nitro CTF and race), every saved
  field of a plain kind is changed in turn and the snapshot's bytes (and `StateHash`) must change with it; a
  field the audit says is saved but whose `Save` forgot it fails here.
- `SnapshotCorpusTests` (the proof): every base mission and every Nitro mission (with `I76_NITRO_INSTALL`), at
  20 and 28 Hz, driven by inputs that are a fixed function of the tick (`SnapshotRig.Input`: throttle,
  steering, the fire key and hardpoints, pad axes, the cockpit's arms and handguns, hotkeys, the ignition, the
  skip key from 16 s), snapshotted at 10 s, on the first skip-key tick, at 60 s, and at the first tick of
  each moment it looks for (an objective done, rounds in flight, a dying car, a gate sliding, an A* search
  under way, flames, mines, a script control queue). For each snapshot the original runs on 30 s and its
  whole state is dumped (`StateDump`); a session rebuilt from the install with another rand seed takes the
  snapshot and must dump the same at once, snapshot back to the same bytes, and dump the same after the same
  30 s of the same inputs (and hash the same). Melees too: single-player arenas with combat cars (flames,
  mines, oil, fire, Blox, guided missiles), waves killed off so that the restore must make the next wave's
  cars, a self-destruct, network melees whose players joined the original but not the rebuild, the Nitro
  race and CTF; and the typed cheats (getdown, flat tyres). Each test asserts which situations its
  snapshots caught (a cut-scene, the skip latch, a gate mid-slide, a dying car, a removed car, cars the
  restore had to make, ...).

`StateDump` is independent of the serialisers: it walks every field by reflection, skipping only those marked
transient; structural fields are shown shallow (values, and references by identity), everything else in full.
Cars, entities, structures and rounds are named by their ids and dumped in their own lists; dictionaries and
sets are in key order; static geometry is a checksum. Whatever a serialiser misses shows as a difference.

They take about a minute and a half with both installs (`dotnet test tests/I76.Sim.Tests --filter Snapshot`).

## Adding state

1. Add the field. `TheAuditNamesEveryFieldOfEveryAuditedClass` fails.
2. If it changes what the simulation does next, write and read it in the class's `Save`/`Load` (in
   `src/I76.Sim/Save/`, mostly `partial` parts of the classes themselves), and add it to the audit's `Saved`
   list. A reference goes as an id (`SaveWriter.Car`, `Ent`, `Obj`, `Path`, `Refs.WriteWeapon`).
3. If it does not, mark it `Structural`, `Derived` (and recompute it in `Load`) or `Transient`, with the
   reason.
4. A new class reached through a saved field needs its own audit entry (`SaveAudit.Build` lists the
   providers).
5. Bump `SnapshotHeader.CurrentVersion`.
6. Run the snapshot tests. `StateHash` follows by itself.

## Checkpoints

The first user (Play Options > Checkpoints; docs/deviations.md). `CheckpointPolicy` (`src/I76.Sim/Save/Checkpoints.cs`)
says when one is due: in a scripted single-player mission, once the intro's cut-scene is over (if nothing was
kept yet) and each time an objective gains its Succeeded flag; never with the player's car dead or the
mission's end decided. `Main` (`game/Scripts/Boot/MainCheckpoints.cs`) asks it once a frame after the ticks,
keeps the latest snapshot in memory with the game's own (the trip's salvage pool, the music's place, the chase
camera's state and the view), and shows the notice. "Retry from Checkpoint" reloads the scene as Replay does;
`SpawnCar` builds the mission (at the checkpoint's tick rate), restores the snapshot, makes or drops car nodes
for `RestoreResult`, loads the salvage pool, calls `DamageView.Resync` and, after the player's views exist,
puts the camera back; `SimDriver.Resume` goes on without a tick 0 and picks up the CB line; the music seeks to
its place and the briefing is not played; Modern Physics is created after the restore, so its body takes the
restored car's pose and motion. `CheckpointTests` checks the policy and a T14 retry against the checkpoint's own
state; the captures `I76_CHECKPOINT_SHOT` / `I76_CHECKPOINT_HOLD` give a before/after pair of the frame.

## What the game layer re-syncs after a restore

The sim's objects are restored in place where they exist, so the game's references to cars, the session, the
combat and the script stay good; drivers are kept when their slot and car match. Then:

- **Car nodes**: make one for each car in `RestoreResult.Created` (from `Vehicle.Definition`), free those of
  `Dropped`, and refresh every car's look from the sim (damage zones, blown tyres, detached parts, lights,
  wreck, the helicopter or emplacement state): `DamageView.Resync()` does it, and the structures below.
- **Structures**: the views follow destruction by its events (`StructureDestroyed`, `SignKnocked`), so after a
  restore set each structure's look from its state: `StaticObject.Nodes`, `Destroyed`, `Collapsed`, the
  gates' `GateMotion.Shift`. Modern Physics' colliders follow `StaticObject.Revision` by themselves.
- **Combat's views**: rounds, flames and lying mines and drops are drawn from `Combat` each frame; effects
  and sounds already playing are not in the snapshot (the views' events pending at the save are, and play
  again).
- **Camera**: the script camera (`ScriptHost.View`) is restored; the game's own chase and cockpit cameras
  start fresh at the player's car. Nothing of the view reaches the simulation (the near/far test measures from the
  player's car).
- **HUD**: radar target and range, weapons, objectives, the map: read from the sim each frame.
- **Sounds**: the engine loops and the CB line on the air (`CbRadio.Current`, started at `StartedMs`) start
  again; one-shots are not replayed.
- **Game-side state that is not the sim's**: the input latch and `CommandQueue`, `AutoReverse`'s hold, the
  cheat typer, Modern Physics' own body (rebuild it from the car, or switch it off and on), the trip's
  salvage pool (pass it as the snapshot's `host` bytes), the play options (apply them again; the snapshot's
  copies of difficulty and unlimited ammo/armour are what the session had).
- The interpolation's previous poses: set them to the restored ones so nothing slides in from the old place.

## What the equivalence test found

- The Ptable's bridge tree is built when a planner first asks for it: a restored session built it at once
  (a dump difference, though not a behaviour one). The snapshot now keeps whether it was built.
- A structure whose last live node dies keeps the bounds of the last rebuild that left geometry: a restore
  that rebuilt the geometry once from the intact shape got other bounds, which still decide whether a splash
  reaches the root. The bounds are saved.
- Found while writing the serialisers, and covered by the test: each car's damage pick table has the
  specials' rows drawn from `rand()` when the car is built, so they differ with the build's seed and are
  saved; the poetry decks use a .NET `Random` whose state cannot be set, so they are dealt again from their
  seed and the count of poems dealt (in a Nitro mission, of the driver's lines dealt).
- `StateHash` covered the cars, rounds, structures' hit points, drivers' keys and melee scores, but none of
  the AI's state, the FSM, the CB radio, the script camera, the gates' targets, the flames, the specials' on
  times, the helicopters' and emplacements' blocks, the melee's timers, the simulation clock and more (the
  mutation test listed over 200 saved fields it missed). It is now the hash of the snapshot's body, so it
  covers everything the snapshot does, and the guard keeps it so.
