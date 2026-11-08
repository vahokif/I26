# Frame rate: the tick is the frame

An implementation spec for making the simulation's tick rate a setting.
Every measurement is cited.

**The model.** The original stepped its simulation once per rendered
frame, so its frame delta *was* its step. The measurement build of the original and this
port both decoupled drawing from stepping. Each uses a fixed step and
draws interpolated poses. So the port's tick rate plays the role of the
original's frame rate. The port runs the original's per-frame code once a
tick with `dt` = the tick, so at tick `1/V` it *is* the original running
at V fps, frame-coupled rules included. Nothing gets re-formulated per
rule. The work is to:

1. make the tick a parameter;
2. find what in the port assumes 0.036 s;
3. choose the tick values precisely, because the original has knife edges
   at exactly 50 ms (§2).

The default becomes **20 Hz**, the rate the community plays the original
at. The tick for it is **102/2048 s = 0.0498046875 s**, not 0.05 s (§2).

Contents:

- §1 the original's clocks and what changes with V;
- §2 the 20 Hz knife edge, and the tick values to use;
- §3 making the tick a parameter in the port: every place to change;
- §4 the inventory of frame-coupled rules (what the setting changes);
- §5 measurements of the real game at 15–59 fps;
- §6 the test plan;
- §7 recommendations: values, default, FlameHold, multiplayer, rendering
  at 20 Hz.

The runs (`fps-{drive,turn,fire}-<ms>`, named by the frame time in ms) were
recorded in the original game with its frame time forced; each holds the
environment, a log and a motion CSV, kept in the maintainers' private notes.

---

## 1. The original's clocks

From the maintainers' notes on the original's frame-rate coupling and vehicle
dynamics, checked where noted.

- **The frame delta.** The original's frame clock computes it once per
  frame from `GetTickCount()*0.001f`, clamped to [0.001, 0.2] s.
  The raw frame delta and, in single player, the sim delta
  (also ≤ 1.0) are the same value. There is no fixed
  rate.
  - The delta is quantised by float32 at the clock's magnitude. At the
    reference machine's uptime the ULP is 1/2048 s. A 20 fps frame
    therefore measures 102 or 103 ULP (49.805 or 50.293 ms), and a 28 fps
    frame 73 or 74 ULP (35.645 or 36.133 ms). Both are seen in every run
    here (`M.CSV` `delta`).
- **The substep** (read from the original's code):

  ```
  n = ftol(frameDelta * acc[0]) + 1    // acc[0] = (float)(1/0.05f) = 20.0f exactly
  if (n > 20) n = 20
  acc[1] = recip[n] * frameDelta       // 1/n
  ```

  Only the vehicle step is substepped,
  inside the original's near path. Everything else runs once a frame.
  `ftol` truncates. So **n = 1 for a delta below 0.05 s and n = 2 from
  exactly 0.05 s** (0.05f × 20 = 1.0000000149 → 1, +1 = 2; in float32 the
  product rounds to exactly 1.0 and gives the same). The port's
  `VehicleStep.Substeps` (`src/I76.Sim/Vehicles/VehicleStep.cs:27`)
  computes the same and gives `(2, 0.025)` for 0.05f.
- **No clock at all.** Some rules add a constant per call: the sky scroll,
  2 smoke puffs a frame, one gauge digit a frame, the death camera's 8° a
  frame, `camTransObj`'s 0.3 a frame, the throttle key's ramp step. At
  tick `1/V` these naturally match the original at V once they run per
  tick.

**Clamps and thresholds on dt.**

- No dt clamp engages in the range offered (15–60 Hz):
  - the clock clamp starts at 0.2 s;
  - the `k ∉ [0, 0.95]` snap of every `ease(cur, target, k)` with
    k = m·dt needs dt > 0.2375 s at the largest m (4, the third-person
    camera).
- The thresholds that do fall inside the range:
  - the substep rule's 0.05 s;
  - the weapon loop's `dt <= A` equality and its `dt < 0.01` remainder
    discard (§2);
  - the integer truncations `ftol(dmg·dt)` in the flame burn (§4).

---

## 2. The 20 Hz knife edge, and which tick values to use

Two of the original's rules change discontinuously at a frame time of
exactly 0.05 s:

1. **The substep.** At ≥ 0.05 s the vehicle step runs twice, at half the
   delta.
2. **The weapon timing loop** (port
   `Combat.Timing`, `src/I76.Sim/Weapons/Combat.cs:530`).
   - After a shot, timer A = 1/rate = 0.05f for a 20/s weapon.
   - The trailing `if (dt < A)` does not fire at dt == A, so A stays 0.05.
   - The next frame takes the `dt <= A` branch and only counts down, so
     no shot.
   - **At dt == 0.05f exactly, a 20/s weapon fires every other frame**:
     10/s, a lost round every frame.
   - At 0.0498 or 0.0503 it fires every frame. At 0.0503 the remainder
     after A is under 0.01 and is discarded within the same frame.

The real game never sees exactly 0.05000 s. Its deltas are
quantised (49.805 / 50.293 ms at 20 fps, measured), so these edges hardly
show. **The port at a tick of exactly 0.05f sits on both edges
permanently.** Measured:

| tick (port) | 20/s gun (7.62 MG) | Napalm Hose stream (without FlameHold) | vehicle step | port vs original 20 fps drive |
|---|---|---|---|---|
| 0.05 | `1010…`, 10/s | stub (2, freed, 2, …) | 2 × 25 ms substeps | −6 m/s at 10 s, −5.6 at 13 s (like the original at 40 fps) |
| 102/2048 = 0.0498046875 | `1111…`, 20/s | 19 cross-sections | 1 × 49.8 ms | −0.5 m/s at 10 s, −0.8 at 13 s |

Sources:

- The weapon rows are from a throwaway port test (`Combat.Frame(dt)` with
  `vppirna2`; the patterns are exactly as shown) and from the Python
  transcription of the weapon loop. The original's own runs in §5
  agree.
- The drive rows are from the port stepped with the fps-drive-50 trace's
  controls, forcing a constant delta.

**Rule: never use a tick of exactly 1/rate for a weapon rate, or exactly
0.05 s.**

- Weapon rates: 0.2–0.5, 1, 2–4, 4.5, 10, 15 (`.45`), 20 (MGs, flamers),
  60 (droppers).
- So an exact 1/15, 1/20 or 1/60 s tick halves the `.45`, the 20/s guns
  and flamers, or the droppers.
- Use multiples of the original's own clock quantum, **k/2048 s**. They
  are exact in float32 and are what the reference machine actually
  produced.

| offered as | tick s | = k/2048 | true rate Hz | substeps | notes |
|---|---|---|---|---|---|
| **20 (default)** | 0.0498046875 | 102 | 20.08 | 1, 2 in 5 ticks 2 | the community's rate; the reference clock's most common 20 fps delta. The world frame follows the clock's 20 fps sequence (below) |
| 25 | 0.0400390625 | 82 | 24.98 | 1 | |
| 28 | 0.036 (kept) | 73.73 | 27.78 | 1 | the reference machine's mean; all existing tests and traces |
| 30 | 0.033203125 | 68 | 30.12 | 1 | avoids Pyro-Tomic's ftol edge at exactly 1/30 (§4) |
| 40 | 0.0249023438 | 51 | 40.16 | 1 | |
| 60 | 0.0166015625 | 34 | 60.24 | 1 | avoids the droppers' 1/60 interval |

0.036 stays for "28" because every existing test, trace and reference run
is at 0.036. It is not on any edge.

**The 20 fps frame sequence.** The tick stays 102/2048 s, but the original
at 20 fps never ran a constant 102: its clock reads 102.4 quanta a frame as
102, 102, 103, 102, 103 (fps-drive-50: 288:225; T09 at 50 ms: 417:279), and
the 103s (50.293 ms) are over the substep edge, so 2 frames in 5 stepped
the cars twice. A constant 102 never takes the second step. The vehicle
step's terrain pin overshoots a slope break by the step's length, so with
one 50 ms step every frame the car left the ground at nearly every crest it
crossed fast. T09's straight south of the start, taken at 28–46 u/s: 9 of
10 speeds airborne, 13.2 s in the air, against 2 speeds and 3.8 s at 28 Hz;
24 keyboard drives of T09 at 30–40 u/s spent 70 s airborne (2 wrecks)
against 28 s at 28 Hz. So at 20 Hz the world frame (`SimRate.FrameDelta`:
the AI, contacts and vehicle steps) takes the clock's sequence by tick
number: 4.2 s and 2 speeds on the crest, 32 s over the drives, no wrecks.
Weapons and scripts keep the 102/2048 tick (the 20/s guns fire every frame
at either delta, §2), so the world runs 0.4 % ahead of them on average, as
the original's 50 ms frames ran against the port's tick. The m01 drive
against fps-drive-50 stays within the §6 tolerance (−1.2 m/s at 10 s,
−1.9 % at 12 s). The other offered rates stay on one side of 0.05 s and
keep their tick (`T09CrestTests`,
`SimRateTests.TwentyHzFramesFollowTheReferenceClocksSequence`).

**The community's 20 fps.**

- The community patch's own limiter (`I76PATCH.DLL`) runs at 60 fps with focus and 20 without. The
  20 fps habit is therefore players' own cap (e.g. a wrapper's limiter),
  not the patch's.
- With any real clock their deltas jitter around 50 ms:
  - the community exe masks GetTickCount to 23 bits, which gives a
    0.5–1 ms quantum;
  - the Windows tick granularity adds up to 15.6 ms.
- A transcription of the weapon loop runs such sequences:
  - 50 ± 1 ms uniform;
  - 46.9/54.7 alternating (one day's uptime);
  - 62.5/46.9 (a 15.6 ms tick).
- All of them give 20/s and a full plume. Only an exact 50.000 ms is
  pathological. That matches players' report that "the napalm works at
  20".

---

## 3. Making the tick a parameter in the port

### 3.1 The parameter

```csharp
// src/I76.Sim/Session/SimRate.cs (new)
public readonly record struct SimRate(int Label, float TickSeconds)
{
    public static readonly SimRate R20 = new(20, 102f / 2048f);   // default
    public static readonly SimRate R25 = new(25, 82f / 2048f);
    public static readonly SimRate R28 = new(28, 0.036f);
    public static readonly SimRate R30 = new(30, 68f / 2048f);
    public static readonly SimRate R40 = new(40, 51f / 2048f);
    public static readonly SimRate R60 = new(60, 34f / 2048f);
    public static readonly SimRate[] Offered = [R20, R25, R28, R30, R40, R60];
    public static SimRate FromLabel(int hz) => Offered.FirstOrDefault(r => r.Label == hz, R20);
}
```

`SimSession` takes a `SimRate` at construction. `SimSession.TickSeconds`
stops being a `const` and becomes an instance property; there is no
global. A session's rate never changes while it runs. Changing the setting
applies from the next mission start or resume, as Graphic Detail's 1997
Mode already does.

Keep the literal **only as a name for 28 Hz**, e.g.
`SimRate.R28.TickSeconds`, in tests that assert 28 fps facts.

### 3.2 Every place to change

**Sim core** (`src/I76.Sim`). Most of it already takes `dt` from the
caller and needs nothing. These are the exceptions.

| where | today | change |
|---|---|---|
| `Session/SimSession.cs:51-52` | `public const float TickSeconds = 0.036f` | instance property from the `SimRate` |
| `Session/SimSession.cs:147,153,158,239,289` | `World.Frame(TickSeconds)`, `Script.AfterFrame`, `Combat.Frame`, the key ramps' `const float dt = TickSeconds`, `arms.Step` | keep, now reading the instance value. The key ramps (`:243-246`) then add 0.4·(1 − t/3) per tick: at 20 Hz full throttle takes 3 ticks = 0.15 s, which is the original at 20 |
| `Vehicles/VehicleStep.cs:199` | `FrameDelta = 0.036f` default | initialise from the rate (it is overwritten by `Frame` at `:117`, but `CheapFrame` and `Rejoin` read the parameter, so it is only a default; set it anyway) |
| `Missions/ScriptHost.cs:92` | `FrameDelta = 0.036f` | **initialise from the rate.** `MissionScript.AfterFrame` (`Missions/MissionScript.cs:49`) sets it only *after* the first `World.Frame`, so on tick 0 the AI controllers (`Ptable.Dt`, `Ptable.cs:42`; `AiDriver`) read the default |
| `Weapons/Combat.Droppers.cs:42` | `FrameDt = 0.036f` | initialise from the rate (set per frame at `Combat.cs:105`) |
| `Session/AutoReverse.cs:15` | `HoldTicks = 8` (port-only option) | express as 0.29 s → `ceil(0.29 / tick)` |
| `Missions/MissionScript.cs:47` | clock held for the first 3 frames | keep per tick (the original's 3 warm-up frames, the original's frame clock) |

The following need nothing. Each runs once a tick with the tick's dt,
which is the original's per-frame behaviour at V:

- `VehicleStep.Substeps`;
- `Vehicle.Engine.Update` (k = 2·frameDelta, applied once per substep with
  the *frame* delta, as the original does, `Vehicle.cs:111-164`);
- `CrewArms.Step` (time based, 0.1 s a strip frame);
- the FSM (`FsmVm.Tick`, once a tick);
- `Ptable`'s LOS round-robin (`Ptable.cs:200-205`, one AI a tick);
- `PtableTactics.cs:624`'s per-frame skid chance;
- `ScriptCamera.cs:170`'s 0.3-a-frame ease;
- `Heli.cs:115-134`;
- the damage model's per-frame `rand()` rolls;
- `Combat` (timing loop, MG batching, turret slew, radar turn, burn,
  fire-drop);
- `SimWorld.Frame`.

**Network** (`src/I76.Net`, `src/I76.Server`). The server's rate applies
to everyone.

| where | today | change |
|---|---|---|
| `I76.Net/Protocol.cs:16` | `Version = 6` | bump to 7 |
| `I76.Net/Protocol.cs:20` | `TickSeconds = SimSession.TickSeconds` const | remove; the session's rate |
| `I76.Net/Protocol.cs:112-158` `JoinAccept` | no rate | add `float TickSeconds` (or the label byte); the client builds its session and `ClientWorld` from it and refuses an unknown value |
| `I76.Net/Protocol.cs:22,24` | `InputHistory = 6`, `MaxQueuedInputs = 8` ticks | keep as ticks but check them in seconds: 6 ticks is 0.30 s at 20 Hz and 0.10 s at 60. Either make them `ceil(0.2 s / tick)` and `ceil(0.29 s / tick)`, or accept the range |
| `I76.Net/MeleeClient.cs:41` | `BufferTicks = 64` | 3.2 s at 20 Hz, 1.1 s at 60: keep, or `ceil(2.3 s / tick)` |
| `I76.Net/ClientWorld.cs:128-132` | `Protocol.TickSeconds` | the joined rate; `Delay` (seconds) must stay ≥ 2 ticks: at 20 Hz ≥ 0.1 s |
| `I76.Net/ClientWorld.cs:214` | `StaleTicks = 10` | 0.5 s at 20 Hz; make it `ceil(0.36 s / tick)` |
| `I76.Net/InputQueue.cs:101` | ring of 64 | fine |
| `I76.Server/NetMeleeServer.cs:173` | scores every `Round(1/Tick)` ticks | from the rate (already a formula) |
| `I76.Server/NetMeleeServer.cs:215` | `next += Protocol.TickSeconds` | the session's rate |
| `I76.Server` host options | — | a `--rate 20|24|25|28|30|40|60` option, default 20; shown in the lobby |

Snapshot bandwidth scales with the rate: one snapshot per tick, so
−28 % at 20 Hz against 28, and +116 % at 60.

**Game layer** (`game/Scripts`).

| where | today | change |
|---|---|---|
| `Sim/SimDriver.cs:36` | `const float Tick = SimSession.TickSeconds` | `Session.TickSeconds` (and the remote's rate); `:223-260` accumulator and alpha unchanged otherwise |
| `Sim/DriveTest.cs:70` | `/ SimDriver.Tick` | the instance value |
| `Shell/Shell.cs:774-805` settings | — | new `[port] sim_rate` (int label), default 20; settings version +1. **Migration:** an existing file without it gets 20 too, and the release notes say so. Never rewrite other keys |
| `Shell/Overlays.cs:119-157` Play Options | — | a "Frame rate" choice (20/25/28/30/40/60), saved/cancelled like Auto Reverse; greyed and showing the server's value in a network game |
| `Sim/FlameStreams.cs:63` | `(int)(_t / 0.036) & 3` (the tube frames cycle once a frame) | `/ tick`, or better count ticks |
| `Shaders/sky_dome.gdshader:24`, `sky_modern.gdshaderinc:15` | `scroll_per_second = 0.02997` (30/1001) | 1997 look: `V/(1001 − 2⁻⁷)` per second with V = 1/tick (0.0201 at 20 Hz); the modern look may keep its own |
| `Sim/DamageSmoke.cs:16-31,226-237` | 1997: 2 puffs and 1/20 of life **per render frame** | 1997: per tick (2 a tick, life 20 ticks = 1.0 s at 20 Hz). Modern: unchanged |
| `Sim/Contrails.cs:16`, cluster trail | 4 segments a frame (render frame) | 1997: per tick |
| `Sim/Cockpit.cs:417-433` `Roll` | the ammo digits step once per render frame | per tick (at 144 Hz a digit rolls 7× faster than the original at 20) |
| `Sim/Cockpit.cs:473-481` `Wheel` | float ease on the render dt (the original's integer truncation, which stops the wheel above ~30 fps, is not kept) | fine as is (a presentation fix); note it in deviations.md |
| `Sim/Cockpit.cs:164` | binocular glance at 30 steps/s | per tick (the original: per frame) |
| `Camera/ChaseCamera.cs:73-110` | eases 2·dt on the **render** delta | fine (presentation); with the render delta the time constant is the continuous one (≈ the original at any V within 5 %) |
| `Sim/Modern/ModernCar.cs:68,570` | `StickBreak = 0.5f/0.036f` (modern physics only) | keep 0.036 as a named constant: the modern model is a deviation tuned at it |

**Tools and tests.**

| where | today | change |
|---|---|---|
| `tools/I76.Cli/MissionRun.cs:21,95` | `Tick = SimSession.TickSeconds` | a `--rate` argument, default 20 |
| tests with literal `0.036f` (DamageTests, CombatTests, VehicleStepTests, HeliTests, MissionScriptTests, ScriptCameraTests, EmplacementTests, PlayerHeliTests, StructureDamageTests, FsmVmTests, ForceFeedbackTests) | the 28 fps behaviour | **keep**: they assert facts of the original at 27.8 fps (shot patterns `110`, the stub, burn 1 per point). Rename the literal `SimRate.R28.TickSeconds` where it is the rate, and add 20 Hz cases (§6) |
| tests using `SimSession.TickSeconds` (SessionTests `:69-75,167,259-280,522`; Net `BotTests`, `NitroModeTests`) | the session's tick | they measure seconds and should pass at any rate; run the suite at 20 and 28. `BotTests.ThreeBotsPlayAMinute…` is already flaky under load |

### 3.3 What existing traces and tests need regenerating at 50 ms

- **Nothing that replays a recorded trace.** Reference mode steps each
  row with the trace's own delta.
- **Tests asserting 28 fps facts stay as they are.** These are
  CombatTests `ATwentyAShotGunFiresTwoTicksInThree`, the napalm stream
  theory, the burn of 1 per point, and everything with a literal 0.036.
  They describe the original at 27.8 fps, which remains an offered rate.
- **New 20 Hz references** are the fps-*-50 and fps-*-49 runs (§5), and
  the §6 cases.
- **Docs that quote "the 36 ms tick" as the port's behaviour** need a
  qualifier ("at 28 Hz"). Mainly these:
  - the "Flame streams' length" row in deviations.md;
  - deviations.md rows "Sim clock", "Engine smoothing", "Weapons and
    rounds clock", "Driving keys' clock", "Sky scroll";
  - multiplayer.md "Key ramps";
  - architecture.md "Time".

---

## 4. Inventory of frame-coupled rules

What the setting changes. Kinds of coupling:

- **dt** — linear in dt, frame-rate independent apart from integration
  error.
- **ease** — `cur += (target − cur)·m·dt`: over a second it tends to
  `1 − e^(−m)` but is not equal at finite dt. Mildly rate dependent.
- **frame** — a fixed amount per frame. The rate in time ∝ V.
- **edge** — a threshold or truncation on dt.
- **rng** — `rand()` draws per frame or substep. They shift the shared
  CRT stream, so the AI's decisions differ with V; the run is still
  deterministic at a given V.

Significance is for gameplay. "At 20 vs 28" gives what a player notices
moving from the current 27.8 Hz to the new default.

| # | rule | port | kind | at 20 vs 28 Hz | sig. |
|---|---|---|---|---|---|
| 1 | vehicle step integrator: Euler velocity, ½at² position, per-substep clamps (angular ±π/2, π, π/4; traction; 4-unit bounce; speed < 1 cut-offs), the terrain pin and landing | `Vehicles/VehicleStep.cs` | dt + edge (per-substep clamps, n at 0.05) | **depends on the substep size** (§5.1). On flat ground within 2 %. Downhill the car gains more speed at larger steps: +3 % at 12 s against 28 fps, +19 % against 59 fps. Turning slides less at 20 than at 59 | high |
| 2 | substep rule n = ftol(Δ·20)+1, n ≤ 20 | `VehicleStep.cs:27` | edge at 0.05 s | 20 fps straddles it: 2 frames in 5 (103/2048 s) take 2 substeps, which the 20 Hz world frame reproduces (§2). At exactly 0.05: 2 × 25 ms every frame, which plays like 40 fps | high |
| 3 | weapon rate / burst loop on the raw delta; MG and `.45` batched with `maxDt/3` pre-advance | `Combat.cs:530-607` | edge (dt == A; dt < 0.01 discard) | 20/s guns (7.62 MG, flamers) fire every frame at 20 (20.0/s) and `110` at 28 (18.5/s). 10/s and 3/s guns unchanged. Measured §5.3 | high |
| 4 | flame stream length: +1 cross-section per shot (cap 19), −2 per frame without one | `Combat.Droppers.cs:66-94`, FlameHold `:71-80` | frame | **long plume at ≤ 20.4 fps, about half length at 25, a stub at 27.8 and above.** The port's FlameHold deviation gives the plume at every rate (§7.3) | high |
| 5 | flame burn per touching point per frame `max(1, ftol(dmg·mult·dt))`; a point burns only if its segment enters the box; the original's cockpit mirror pass burning again every second frame (×1.5) is left out (deviations.md) | `Combat.Droppers.cs` (`FlameFrame`, `FlameTouch`) | frame + edge (ftol) | per point per second at 20.08 / 27.8 / 60.2 Hz: FlameThrower (15) 20 / 28 / 60; Gas Launcher (30) 20 / 28 / 60; Napalm Hose (45) **40** / 28 / 60; Pyro-Tomic (60) 40 / 56 / 60 (30 at R30). Edges: Napalm 2 per point below 22.5 Hz; Pyro 2 per point below 30 Hz (exactly 1/30 → 2.0000001 → 2) and 3 at exactly 0.05 | high |
| 6 | Fire-Dropper damage: 15 every frame of contact, not dt-scaled (×2 / ×0.75 for the player's by difficulty) | `Combat.Droppers.cs:241-250` | frame | 15·V per second in contact: **301/s at 20 vs 417/s at 28** (900 at 60) | high |
| 7 | radar missile turn: per frame `min(0.75·|f×d̂|, cap)`, cap 0.0523 / 0.2588 / 0.3420 rad by distance flown (none at exactly 150 m) | `Combat.cs:876-887` | frame | max turn rate cap·V: 5.2 rad/s at 20 vs 7.2 at 28 (mid-range). Turn radius at vT: DrRadar (125) 24 m vs 17 m; Cherub (450) 87 m vs 63 m. Missiles miss more at 20 | high |
| 8 | AI driving actuators: steering and throttle computed to reach the target in one frame (∝ 1/dt) | `Ai/AiDriver.cs` | dt (1/dt) | the demands scale with 1/dt, so the AI steers differently at each rate (the AI over-steers if the simulation rate differs from the frame rate); at tick 1/V it is the original's AI at V | medium |
| 9 | AI per-frame cadences: one LOS raycast per frame round-robin; slot = frame counter mod 4; tactic_skid one chance in 8 + 40(1 − skill) per frame; the per-frame fire decision | `Missions/Ptable.cs:200-205`, `PtableTactics.cs:624` | frame | each AI sees the player 28 % less often in time; skids are 28 % rarer | medium |
| 10 | mission-script VM: every thread once per frame; timers read the sim clock | `Fsm/FsmVm.cs:63`, `Missions/MissionScript.cs:47` | frame (polling) | waits and timers are in seconds; per-frame polling just quantises them to the tick (50 ms) | low |
| 11 | `rand()` per substep or frame: rough terrain (every substep over 7.65 u/s), skid, blown tyres, damage smoke rolls, death effects `rand&15` per substep | `Damage/DamageModel.cs:168-246`, `VehicleStep` | rng | the AI's numbers differ with V: same rate, different game. Deterministic at a given V | medium |
| 12 | engine rpm and speedo smoothing k = 2·frameDelta, applied once per *substep* with the frame delta | `Vehicle.cs:111-164` | ease (+ ×n below 20 fps) | negligible at 20 vs 28 (1 substep); doubles below 20 fps | low |
| 13 | attitude rate limit toward target by 2·dt, snap if k > 0.95 | `VehicleStep.cs:1005-1006` | ease | negligible | low |
| 14 | rounds: velocity eases to terminal by k = rawDt (bullets), 0.5·rawDt (rockets), dt (others) | `Combat.cs:872-873` | ease | rockets reach speed in about the same time; within a few % | low |
| 15 | turret slew dt·h·1.7 rad (×6.8 heli); fixed guns' pitch ease 4·rawDt | `Combat.cs:477`, `:500-523` | dt / ease | negligible | low |
| 16 | cluster bomblets also subtract rawDt per frame from the pending time (bug) | `Combat.BombletFrame` (per tick, the tick's delta) | dt (double) | they go off within about 0.9 s at any rate; the frame a slot falls due shifts | low |
| 17 | droppers' placement uses the frame delta (`speed·dt·0.2`, `(speed·dt + 1.5)·u`) | `Combat.Droppers.cs:226-230` | dt | drops land up to 0.7 m farther apart at 50 m/s (the drop spacing follows the frame step) | low |
| 18 | network weapon jam: on a jam `ammo −= ftol(dt·rate)`, one `rand()` per trigger frame | `Combat.Jam` (per tick, server only) | edge + rng | the jam's chance is per tick: at 20 Hz a held damaged weapon jams 20 % of its ticks where 28 fps jams 20 % of its frames; the ammo lost per jam follows the delta (a 20/s MG loses none at 28 fps, ftol(0.996) = 0 at 20 Hz too) | low |
| 19 | driving keys: throttle +0.4·(1 − t/3) a frame, −0.5·(1 − t/3) braking; steering √(t/3) by hold time | `SimSession.cs:239-246` | frame | full throttle 0.15 s at 20 vs 0.11 s at 28; steering by time, unchanged | low |
| 20 | scripted camera `camTransObj` x/z eased 0.3 per frame | `Missions/ScriptCamera.cs:170-171` | frame (no clock) | the camera glides 28 % slower in time at 20 | low |
| 21 | helicopter (Nitro): `ease(thr/side, k = dt)`, height `ease(…, 0.5·dt)` | `Vehicles/Heli.cs:115-134` | ease | negligible | low |
| 22 | sky scroll 1/(1001 − 2⁻⁷) per frame | shaders (per second) | frame (no clock) | 0.020/s at 20 vs 0.028 | low (look) |
| 23 | damage smoke: 2 puffs per frame, life 20 frames | `DamageSmoke.cs` (render frames) | frame | 1.0 s trails at 20 vs 0.72 | low (look) |
| 24 | third-person camera: yaw += 4·rawDt·Δ (no clamp), pitch/roll snap above 0.95 | `ChaseCamera.cs` (render dt, 2·dt) | ease | none visible | low (look) |
| 25 | death and end-of-match orbit 8° per frame (one view for both, no clock) | `Views/DeathOrbit` (8° per tick's worth of time, on the render delta at the session's rate: the original at that sim rate) | frame | 160°/s at 20, 222°/s at 28, 482°/s at 60, smooth at any display rate | low (look) |
| 26 | cockpit: ammo digits roll one step per update; steering-wheel strip the steering-wheel animation (integer, truncated: frozen at high fps); binocular glance ±29/31° per frame | `Cockpit.cs` (render frames) | frame / edge | the wheel turns in the original at 20, not at 60 | low (look) |
| 27 | contrails / cluster trail: 4 segments removed a frame | `Contrails.cs` | frame | look | low (look) |
| 28 | scenery spinners rotate ω·rawDt | `ScenerySpawner` (time) | dt | none | low |
| 29 | the clock itself: [0.001, 0.2] clamp, sim delta ≤ 1.0, float32 quantum | fixed tick | edge | none in range | — |

Every use of the raw frame delta (33 places), the sim frame delta (21) and
the sim step rate (8) in the original is either in this table or is one of
these:

- a linear integrator on dt: collision sweeps;
- the AI look-ahead;
- a presentation clock: views, binoculars, the force-feedback block.

---

## 5. Measurements of the real game

Method:

- `tools/wine-run.sh` with `I76_FIXED_STEP_MS=<ms>` (the engine sees a
  constant step, quantised by the float32 clock at `I76_TICK_OFFSET=6999000`
  exactly as on the reference machine) and `I76_SEED=1`;
- m01 Auto Melee, Piranha `vppirna1` (50cal MG on key 1, 25mm Cannon on 2,
  Gas Launcher on 3);
- `I76_DRIVE` spans in seconds converted to frames per run;
- frame dumps off.

The frame cap does not matter under a fixed step: the physics follows the
delta sequence, not the presentation rate (frame-rate-coupling.md, "60 fps
with 1997 physics").

Runs: 67, 51, 50, 49, 40, 36, 33, 25, 17 ms (14.9–58.8 fps). The 50 ms
run's deltas alternate 49.805 and 50.293 ms, 288:225, so 3 in 5 frames
take one substep and 2 in 5 take two.

### 5.1 Straight-line acceleration (`fps-drive-<ms>`)

Throttle from 1.5 s, straight. The ground is flat to about 6.5 s, then a
70 m descent.

| ms | fps | v@4 | v@6 | v@8 | v@10 | v@12 (m/s) | t→30 | t→40 | t→50 (s after throttle) |
|---|---|---|---|---|---|---|---|---|---|
| 67 | 14.9 | 18.8 | 26.5 | 37.4 | 43.5 | 49.8 | 5.29 | 6.97 | 10.59 |
| 51 | 19.6 | 18.6 | 26.3 | 36.4 | 41.8 | 48.2 | 5.46 | 7.14 | 11.12 |
| **50** | **20.0** | 18.7 | 26.4 | 37.9 | 47.4 | 53.5 | 5.30 | 6.80 | 9.20 |
| 49 | 20.4 | 18.7 | 26.5 | 37.8 | 47.2 | 53.0 | 5.29 | 6.81 | 9.31 |
| 40 | 25.0 | 18.5 | 26.4 | 37.5 | 45.3 | 51.4 | 5.32 | 6.88 | 9.92 |
| 36 | 27.8 | 18.6 | 26.4 | 37.3 | 46.2 | 51.9 | 5.33 | 6.95 | 9.72 |
| 33 | 30.3 | 18.7 | 26.4 | 37.3 | 43.7 | 49.9 | 5.35 | 7.00 | 10.53 |
| 25 | 40.0 | 18.5 | 26.2 | 36.4 | 42.1 | 48.1 | 5.45 | 7.17 | 11.25 |
| 17 | 58.8 | 18.3 | 25.9 | 34.4 | 38.9 | 45.1 | 5.63 | 7.72 | 12.07 |

**Yes, the physics itself changes with the frame rate**, and it follows
the *substep size*, not the frame rate:

- 51 ms (two 25.5 ms substeps) plays like 25 ms (v@12 48.2 vs 48.1);
- 67 ms (two 33.5 ms substeps) plays like 33 ms (49.8 vs 49.9);
- 49–50 ms (one ~50 ms substep) is the fastest.

On flat ground the spread is ≤ 2 % (v@6). It grows on the descent: the
body is pinned to the terrain and re-landed every substep, and how much
speed survives depends on the step. Positions against the 49 ms run:
0.1–0.4 m at 6 s (except 17 ms: 1.0 m), and 1–32 m at 12 s.

### 5.2 Turning (`fps-turn-<ms>`)

Throttle 1.5–5.5 s (≈ 24.7 m/s), coast. Full left 5.5–8.5 s (the key
ramp √(t/3)), right 9–11 s. Heading change from 5.5 s, and frames with the
slide flag (0x2):

| ms | fps | v@5.5 | hd@6.5 | hd@7.5 | hd@8.5 | hd@10.5 | v@11.5 | slide frames |
|---|---|---|---|---|---|---|---|---|
| 67 | 14.9 | 24.83 | −30.6 | −80.4 | −139.3 | −81.2 | 19.1 | 18 |
| **50** | **20.0** | 24.77 | −30.2 | −79.5 | −138.6 | −77.7 | 18.4 | 24 |
| 49 | 20.4 | 24.85 | −30.1 | −78.9 | −135.9 | −82.9 | 21.7 | 25 |
| 40 | 25.0 | 24.69 | −28.9 | −77.6 | −135.0 | −78.1 | 21.2 | 30 |
| 36 | 27.8 | 24.73 | −29.2 | −79.6 | −139.5 | −80.8 | 20.1 | 33 |
| 25 | 40.0 | 24.55 | −29.8 | −80.8 | −143.4 | −80.4 | 18.5 | 48 |
| 17 | 58.8 | 24.24 | −29.8 | −84.1 | −153.3 | −85.6 | 18.0 | 70 |

Turning is close to rate independent from 15 to 30 fps (±3° after 3 s
of full lock). At 40–60 fps the car turns tighter and slides 2–3× as
often: the slide test runs per substep. A turn at the grip limit at
~50 m/s (fps-drive, steering from 12 s) is chaotic: the 49/50 ms runs
spun out and the others did not.

### 5.3 Weapon timing and the flame stream (`fps-fire-<ms>`)

Parked; 3 s of each weapon held. Rounds per second over the window
(window rounding ±0.3/s). The pattern is rounds per frame. The stream is
reconstructed from the per-frame shots with the §4 rule 4 counts; it was
not read from memory. In flame4 at 28 fps the stub was also seen on screen.

| ms | fps | 50cal MG (10/s) | 25mm Cannon (3/s) | Gas Launcher (20/s) | pattern | stream while held |
|---|---|---|---|---|---|---|
| 67 | 14.9 | 10.06 | 2.99 | 20.12 | `211211` | 19 (full) |
| 51 | 19.6 | 9.80 | 2.99 | 19.61 | `111111` | 19 |
| **50** | **20.0** | 10.16 | 3.28 | 20.00 | `111111` | **19** |
| 49 | 20.4 | 10.21 | 2.92 | 20.08 | `111111` | 19 |
| 40 | 25.0 | 10.21 | 3.29 | 19.74 | `011110111101` | mean 14, mostly full |
| 36 | 27.8 | 9.27 | 2.98 | 18.52 | `110110` | **stub, 2–3** |
| 33 | 30.3 | 10.21 | 3.29 | 19.76 | `110110` | stub, 2–3 |
| 25 | 40.0 | 10.25 | 2.98 | 20.17 | `010101` | stub, 0–2 |
| 17 | 58.8 | 9.97 | 2.99 | 19.83 | `100100` | stub, 0–2 |

Round rates are right everywhere, within one round per window. 28 fps is
the one rate at which the 20/s gun loses 7 %, because of the `dt < 0.01`
remainder discard. The stream is long only where the gun fires every
frame, at ≤ 20.4 fps. At 25 it is about half length on average.

### 5.4 Not measured, and why

**Radar missile turn and Fire-Dropper damage.** Both need a target in
contact, which an unattended run cannot arrange reliably. Their
dependence comes straight from the code: a constant per frame, rows 6–7.
The port already runs both once a tick, so at tick 1/V they are the
original at V by construction. The §6 unit tests pin the numbers.

**The helicopter climb.** It is Nitro only and an ease (row 21). It is not
gameplay significant.

### 5.5 The port against each rate (free run)

Each trace's controls and deltas are fed to the port from 1 s, with no
resync. Position error and speed error (port − original):

| trace | t = 3 s | 6 s | 9 s | 12 s |
|---|---|---|---|---|
| drive 67 | 0.98 m −0.7 | 2.24 m −0.3 | 4.39 m −0.6 | 5.56 m 0.0 |
| drive 51 | 0.72 −0.4 | 1.61 −0.3 | 2.00 −0.1 | 2.44 −0.3 |
| drive 50 | 0.67 −0.3 | 1.34 −0.1 | 2.34 −0.3 | 5.27 −0.9 |
| drive 49 | 0.58 −0.5 | 1.49 −0.2 | 2.28 −0.3 | 4.54 −0.7 |
| drive 40 | 0.54 −0.5 | 1.48 −0.3 | 3.03 −0.5 | 0.96 +0.7 |
| drive 36 | 0.41 −0.2 | 0.89 −0.1 | 1.58 −0.1 | 3.66 −0.6 |
| drive 33 | 0.41 −0.3 | 0.89 −0.2 | 1.64 −0.3 | 2.44 −0.3 |
| drive 25 | 0.36 −0.1 | 0.72 −0.1 | 1.53 −0.4 | 2.12 0.0 |
| drive 17 | 0.22 −0.1 | 0.64 −0.1 | 1.21 −0.2 | 3.06 −1.6 |
| turn 67 / 50 / 49 / 36 / 17 | 0.98 / 0.67 / 0.58 / 0.41 / 0.22 | 2.18 / 1.26 / 1.43 / 0.95 / 0.54 | 1.70 / 0.90 / 1.30 / 1.01 / 1.24 | 3.9 / 8.0 / 3.9 / 1.8 / 2.4 |

Maximum heading error through the turn: 5.4° (67), 13.3° (50), 5.0°
(36), 3.4° (17).

**The port reproduces the original's frame-rate dependence:**

- the originals differ from each other by up to 8 m/s and 32 m (§5.1);
- the port, fed each rate's deltas, stays within ~1–5 m and 1.6 m/s of
  that rate's own trace.

The small error at t = 3 s grows with the step (0.2 m at 17 ms, 1.0 m at
67 ms). That is a residual port deviation on flat ground that scales
with dt, worth a look but not a blocker.

---

## 6. Test plan

The rule: **the port at tick T, played as label V, must match the
original measured at V fps within tolerance.**

1. **Reference mode, every rate.** The port on m01 with vppirna1.vcf,
   stepped with each fps-{drive,turn}-<ms> trace from 1 s, free run.
   - Assert position error ≤ 2.5 m at 6 s and ≤ 6 m at 12 s, and speed
     error ≤ 2 m/s.
   - Check in the trimmed traces (controls + state columns only, a few
     hundred KB each) as test data. They are recordings of motion, not game
     data.
   - Run 49, 50, 36 and 17 at least.
2. **Play mode at the new default.**
   - Run the fps-drive-50 controls through a `SimSession` at
     `SimRate.R20` (constant 102/2048).
   - Assert v(t) within 1.5 m/s of the trace up to 10 s and within 4 % at
     12 s. The forced-constant replay gives −0.5 m/s at 10 s.
   - **Assert the opposite for 0.05**: a session forced to exactly 0.05 s
     must *not* match. It is ≥ 4 m/s slow at 10 s. This guards against
     someone "simplifying" the tick to `1f/20`.
3. **Weapons** (CombatTests, new theory over `SimRate.Offered`):
   - 7.62 MG patterns (from a transcription of the weapon loop): R20
     `111111111111`, R25 `111101111011`, R28 `110110110110` (existing),
     R30 `110110110110`, R40 `101010101010`, R60 `100100100100`; 20/s
     on average at every rate except 28 (18.5). Also, 0.05f gives
     `101010…` (the edge, documented).
   - Napalm stream with FlameHold off: R20 reaches 19 within 18 ticks;
     R25 hovers at 9–13; R28, R30, R40 and R60 stay ≤ 3.
   - Burn per point per tick: Napalm 2 at R20, 1 at R28; Pyro 2 at R20
     and R28, 1 at R30 (60 × 68/2048 = 1.99), 3 at a 0.05f tick.
   - Fire-Dropper: 15 per tick of contact at every rate.
   - Radar missile: the per-tick turn ≤ cap at every rate. Over 1 s of
     pursuit the heading change is ≤ cap·ticks.
4. **Session suite at 20 and 28.** Run all of `I76.Sim.Tests` and
   `I76.Net.Tests` with the default rate set to each. Tests that pass a
   literal 0.036 keep it.
5. **Network.**
   - A join to a 20 Hz server from a client whose own setting is 60 runs
     at 20 (`JoinAccept` carries it).
   - A version-6 client is refused.
   - `BotTests` at 20 and 60.

---

## 7. Recommendations

### 7.1 Values and default

- **Offer 20, 24, 25, 28, 30, 40, 60**, with the §2 tick values. Label the
  setting "Frame rate (original)" or "Simulation rate". The help line
  says it reproduces the original capped at that rate; drawing stays
  smooth at any rate.
- **Default 20.** This is what the community plays and what the user
  asked for. It is also the rate at which the original's flamers work as
  intended (§5.3).
  - 24 is the step the original takes when it is capped at 20 fps on
    Windows NT and its successors. The original reads its frame time from
    `GetTickCount`, which advances in 15.6 ms steps there, so a 20 fps cap
    measures its frames as 46.9 or 62.5 ms; the substep rule (§1) runs a
    62.5 ms frame as two steps of 31.25 ms. The car is stepped at 46.9 ms
    (about 80 % of steps) and 31.25 ms, about 24 steps a second, where the
    reference clock's 20 fps (§2) gives 28. Anyone can check it: log the
    GOG exe's frame times at its 20 fps cap on Windows 10, or compare a
    1 m drop's bounce tempo there with this port's at 20 Hz. The
    measurements in §5 were made under Wine, whose `GetTickCount` counts
    whole milliseconds, with the step forced, so they do not show this
    split. The tick is 85/2048 s (41.50 ms, 24.09 Hz), the grid's nearest to 1/24;
    the clock's own 85, 85, 86 sequence would also take one substep a
    frame, so the world keeps the tick. Against 20 Hz (102.4/2048 s mean,
    28 vehicle steps a second, §2): 20/s guns keep 20.1/s (`111101111011`);
    without FlameHold the flame stream builds up as at 25 (17-19
    cross-sections after about 40 ticks, 1.7 s; 20 Hz is full in 18);
    Napalm 1 per point per tick, 24/s (above the 22.5 Hz edge; 40/s at 20);
    the Fire-Dropper 361/s; radar missiles, the AI's per-frame cadences and the
    scripted camera's glide 20 % faster in time; full throttle from the
    key in 3 ticks (0.125 s).
  - 28 stays available as the 1997 reference machine's rate, which every
    earlier port measurement used.
  - 60 is for players who want the original at 60, quirks included:
    - stubby flamers;
    - Fire-Dropper burns at 900/s;
    - radar missiles turn 3× as hard;
    - cars slide more in turns and are slower downhill;
    - 3× the CPU cost of 20.
- Below 20 (15): not offered. The original substeps there and nobody
  plays it.

**What players notice moving from today's 27.8 to 20:**

- flamers give the long plume without any deviation, and Napalm burns
  2 per point per tick, ≈ 40/s per touching point instead of 28;
- the Fire-Dropper burns 28 % less (301/s instead of 417);
- radar missiles turn 28 % less sharply;
- the AI reacts 28 % less often (LOS, skids) and steers a little gentler;
- the car is ~3 % faster down long hills and slides slightly less in hard
  turns;
- full throttle from the key takes 0.15 s instead of 0.11;
- in the 1997 look: the sky scrolls slower and damage smoke trails last
  1.0 s;
- the input-to-motion delay grows by ~20 ms (§7.4).

### 7.2 Multiplayer

The server's rate applies to everyone. The host chooses it, default 20,
and it is sent in `JoinAccept`. Clients build their session, input
sampling and `ClientWorld` from it. Their own Play Options value is
ignored and shown greyed while connected. Bump `Protocol.Version`.
Tick-count constants on the wire path are listed in §3.2. Express them in
seconds, or check that they are sane from 20 to 60 Hz.

### 7.3 FlameHold

At the default it does nothing.

- At 20.08 Hz the timing loop fires on every tick the trigger is held, so
  the deviation's extra growth never applies. `FlameHold`
  (`Combat.Droppers.cs:75-80`) returns when the tick already fired, and
  the port at 102/2048 fires on every held tick (pattern `1111…`).
- The same holds at ≤ 20 fps generally.
- At 25 (82/2048) the original's stream hovers at about half length
  (9–13 cross-sections; 14 on average in the 40 ms run), and FlameHold
  tops it up to 19.
- At 28 / 30 / 40 / 60 it is what turns the original's stub into a plume.

Recommendation:

- **keep it, on by default, as a recorded deviation that only matters
  above 20 Hz**;
- reword the deviations.md row: "no effect at the default 20 Hz; at 25 Hz
  and above the original's stream is a stub, which this replaces";
- optionally expose it as "Flame plume at any rate" for purists who pick
  28+ and want the stub.

Removing it would make 28–60 Hz play the stub. That is faithful to
those rates, but it is what testers reported as broken.

### 7.4 Rendering at 20 Hz

The tick is 50 ms, so presentation has to carry more of the smoothness.

- **Interpolation.**
  - `CarNode.Place` lerps position and nlerps the rows between the last
    two ticks (`SimDriver.cs:260`). That is adequate at 20 Hz: 2.5 m and
    ≤ 9° per tick at 50 m/s and π rad/s.
  - Rounds already interpolate on `Alpha` (CombatView).
  - Check everything drawn from sim state for un-interpolated
    placement: flame stream points, turrets, wheels' steer angle, the
    radar blips, damage smoke spawn positions, slicks and fire patches. At
    28 Hz a 36 ms snap was tolerable; at 20 a 50 ms snap visibly steps.
  - Spawn per-tick effects with sub-tick offsets along the motion in the
    modern look, or they appear in 50 ms clumps.
- **Latency.** The display shows the state one tick behind. Input sampled
  just after a tick waits up to one tick before it is applied. Average
  input-to-photon latency in sim terms is ≈ 1.5 ticks: 75 ms at 20 Hz
  against 54 at 28 and 25 at 60.
  - Keep the chase camera and the cockpit head on the render clock, as
    now.
  - Do not extrapolate the player's car; it would mispredict landings
    and collisions.
  - If 20 Hz feels laggy, the cheap fix is to sample input as late as
    possible: just before the tick runs, already the case in `_Process`.
- **The camera.** `ChaseCamera` eases on the render delta, so it is
  smooth at any tick. Leave it.
- **Frame pacing.** With a 20.08 Hz tick and a 60 Hz display the alpha
  beats slowly against vsync. That is fine because interpolation absorbs
  it. Do not round the tick to the display.

### 7.5 The frame rate limiter (Max FPS, VSync)

Graphic Detail's Max FPS (30, 60, 75, 120, 144, 165, 240, Unlimited) sets `Engine.MaxFps`; VSync sets the window's vsync mode
(`FrameLimiter`, shell.cfg `[port]` `max_fps` in frames a second with 0 for none, and `vsync`; absent keys are Unlimited and on, as before).
It limits drawing only. The sim is stepped from `SimDriver._Process`'s accumulator on the frame's delta (clamped to 0.25 s), so a cap below
the tick rate just runs several ticks a frame and the cars are drawn at `alpha` between the last two. Measured with `I76_FPS_LOG=1`
(t01 `--drive --chase`, 960x540 off-screen, vsync off):

| cap | Simulation Rate | frames/s | ticks/s | frame time sd |
|---|---|---|---|---|
| 30 | 40 | 30.1 | 40.5 | 1.9 ms |
| 30 | 60 | 29.8 | 60.5 | 7 ms (GPU spikes) |

The sim rate holds (the tick is 24.9 ms / 16.6 ms, 40.2 / 60.2 Hz) with one or two ticks a frame, the pacing is even, and a sim step is
never skipped or stretched. Input is still sampled per tick, so with two ticks in a frame both see the same keys.
