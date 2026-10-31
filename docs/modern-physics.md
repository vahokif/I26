# Modern physics (an option)

The game's vehicle model is our port of the original's (src/I76.Sim, checked
against traces of the real game). It stays the default and is untouched by
this option. *Modern physics* is a second way to drive: the player's car as a
rigid body in Jolt, Godot 4's 3D physics engine, with suspension and tyres,
on the same terrain and scenery. Everything else (AI, scripts, weapons,
damage, radar, sounds, the HUD) keeps running on the sim and sees the car
as an ordinary sim vehicle.

How to try it: `--modern-physics` on the command line, or F7 in a mission
(it switches at once and is remembered in user://shell.cfg, `[port]
modern_physics`). Without either, nothing in this document runs.

## 1. Choices

### Jolt's VehicleBody3D, or our own raycast car on a Jolt rigid body

Godot's `VehicleBody3D` is not Jolt's vehicle: it is Godot's own scene-side
raycast vehicle (a port of Bullet's), which runs on whichever physics server
is active. Its tyre is one `friction_slip` coefficient with a sideways
impulse solver: no slip curve, no load sensitivity, no friction ellipse, and
known jitter and "ice" at low speed. Its knobs (engine force, brake, suspension
stiffness in its own units) map poorly onto the car's files. Jolt's own
`VehicleConstraint` (engine, gearbox, differentials, a proper tyre) is not
exposed by Godot.

**Chosen:** a `RigidBody3D` on Jolt with our own raycast suspension and tyre
forces in `_IntegrateForces` (game/Scripts/Sim/Modern/ModernCar.cs, about
350 lines). Jolt does what it is good at: the body, its inertia, contacts of
the hull with terrain and scenery, stacking and rolling. Every tyre and
suspension number comes from the car's data or a stated default, and the
code is ours to tune.

### Its own physics world

The car lives in a `SubViewport` with `OwnWorld3D` that is never rendered.
Its coordinates are the game's own numbers, taken as they are (physics does
not care about handedness, so no mirroring), relative to a *physics origin*
that follows the car. The rendered world (mirrored Z, its own floating
origin) never meets it. The visual debris, Godot rigid bodies in the
rendered world, cannot touch the car's colliders.

### Terrain: a moving HeightMapShape3D window

The mission's heightfield is 10240² samples, 5 units apart (51 km). The
physics world holds a 257² window (1.28 km) of the bare samples, the same
ones the sim's ground reads (`IGround.BareHeight`), as a `HeightMapShape3D`.
Heights are divided by 5 and the shape scaled uniformly by 5, since Jolt
wants uniform scales. When the car is 300 from the window's centre, the
window is refilled around it and the origin moves (a few milliseconds). Jolt
triangulates each cell where the sim interpolates bilinearly. The difference
is a few centimetres, and a debug ray checks the two agree
(`I76_DEBUG_MODERN`).

### Scenery

`StaticWorld`'s colliders are already built for the sim: per placement, the
class-0x3000 nodes' polygons, the class-0x2000 node boxes, and the drive-on
surfaces of structures. Each object within 800 of the origin becomes a
static body: its polygons as a two-sided trimesh, its boxes as box shapes.
They are dropped beyond 1000. A destroyed object's body goes; a collapsed
one's comes back as its remains. Signs and pickups (type 4) get no body: the
sim's own sphere test still knocks them over, as before.

### Plugging into the sim

`SimWorld` gained one hook, `External` (a predicate) with `ExternalFrame`.
For a vehicle it claims, the frame:

- still runs the sign test of the object pass (signs are not in Jolt);
- still runs the car pairs (`PairContacts`), so a contact between this car
  and a sim car is found as the original finds it. The sim car answers it in
  its own step, as the original does, and both cars take
  `DamageModel.CarContact`'s damage;
- calls `ExternalFrame` instead of the vehicle step. That runs the step's
  bookkeeping (`VehicleStep.ExternalFrame`: the death and engine-start timer
  per substep, rough-terrain damage) and hands over what the body found
  (below).

When `External` is unset (the option off) every path is the original's,
bit for bit; the sim tests, trace equivalence included, are unchanged.

The body writes its state into the `Vehicle` every physics step: Pos, Rot,
Vel, Speed, AngVel (as the original's body rates, ω_local = −Rᵀω), Accel,
the airborne and sliding flags, ground normal, surface and height, the
wheels' travel, steer and spin. The engine block is the original's
(`Engine.Update`: rpm, the automatic box, the force curve), run on the
body's speed, so the HUD, the tacho and the engine sound follow. The game's
own writes into the Vehicle are noticed (the value differs from what the body
last wrote) and applied to the body: a teleport (scripts, a melee respawn) moves
it, and a kick (the death throw, mines) sets its velocity and spin.

While the mission script holds the camera (cut-scenes), the original's model
drives the player's car, since the scripts' driving was tuned against it.
The body is frozen and takes over again from the Vehicle's state afterwards.

### Damage from impacts

The original's damage inputs are an impact velocity against a normal:
`trunc(m·((−n)·v)²·K)`. The body records each *new* hull contact (none
with that collider for 0.15 s), with the car's velocity at that point one step
before (the solver has already answered the contact by the time it is
reported), and the contact normal. At the next sim frame:

- **terrain** → `TerrainImpact` when the closing speed is over 4 u/s (the
  original's bounce test), with the vtcoll sound;
- **scenery** → `DamageModel.StaticContact` with that velocity: the car's
  zone and the structure's HP as the original's collide callbacks do, and
  the object-hit sound by type;
- **touchdown** after 0.25 s with no wheel on the ground → `Landing` with
  the velocity before; a fall of more than 35 → `FallKill`;
- **cars** → found by the sim's pair test (above).

The sim's own scenery test cannot be used for the body: it predicts the next
36 ms from the current velocity, but Jolt answers the contact between two
sim frames, so the sim never sees the car closing on the wall.

### Car against car

AI cars stay on the original's model and are not bodies in the physics
world. The pair test finds the contact. The sim car responds in its step;
the body takes an impulse along the contact normal at the contact point,
`j = −(1+e)·v_n / (1/m + 1/m_other)` with restitution 0.3. Heavier cars shove
it, it shoves light ones, and an off-centre hit spins it.

Alternatives considered: AI cars as kinematic bodies in Jolt. Infinite mass
makes them walls to the player, and the sim car would still need the pair
test to react. As dynamic "puppet" bodies driven toward the sim pose, Jolt's
answer and the sim's fight each other. Both are worth revisiting if AI cars
move onto the modern model too (§4).

### Ticks

The sim runs at its Simulation Rate with interpolation, Jolt at 120 Hz while the option is
on (`Engine.PhysicsTicksPerSecond`, restored when it goes). The body is
drawn from its own last two physics states, interpolated by Godot's physics
fraction (`CarNode.LivePose`), not from the sim's ticks, which would alias
120 Hz motion to the sim's rate. The sim reads the body's latest state at its
frames.

The body steps at 120 Hz at every Simulation Rate, but its handling follows the
original's at the session's rate (§2.2): the keys' ramp, the yaw's answer, the
drive, the brake and the drifts. The body's controls are read at every 120 Hz step, not at the sim's tick:
`ModernPhysics.Control` takes the live keys or pad (`SimDriver.LiveDriving`)
and writes the Vehicle's throttle, steer and handbrake, which the sim's tick
then reads for the HUD and the engine's sound. The engine (`Engine.Update`
and its force curve through the automatic box) is stepped by the body at its
own speed in the same step. While the body drives, the sim's tick does not
ramp the keys. A test script (`DriveTest`) or the mission script holding the
camera writes the controls itself, as before.

### Determinism

Not a goal in this mode. Jolt is deterministic for a fixed input sequence,
but the frame timing is not. Reference and replay modes keep the original's
model.

## 2. Tuning from the car's data

| What | From |
|---|---|
| mass | the sim's (VDFC + wheels + engine, brakes, suspension, weapons) |
| hull | the COLP box: outer outline at y1 and y2, the cabin (x1..x2, z1..z2) at the roof y3, as a convex hull |
| wheels | the WLOC slots, radius = the slot's height (the original's rest) |
| centre of mass | midway between the axles, 22% of the way from the floor (y1) to the roof |
| inertia | a box of the hull's size (yaw ×1.1) |
| springs | per axle from its static load, 1.65 Hz; damping 0.45 bump / 0.75 rebound of critical; travel +0.16 / −0.18, a stiff bump stop in the last 3 cm; anti-roll 1.1 of the spring rate; a ray hit steeper than 70° is the hull's, not the tyre's |
| drive | rear wheels, as the original's traction test; force = the original's engine force (`Engine.Force`, the ENGN curve through the automatic box) × EngineScale × throttle / max(speed, 1), the original's own formula, × DriveScale (0.90–0.97 by the rate, §2.2) |
| brake | 2300·8·1.4 N at full pedal (the original's BrakeStrength·mass·8, a little stronger), × BrakeScale (0.81–0.88 by the rate, §2.2), 62% front |
| tyre friction μ | the surface's grip from WRLD's table (1.5 dirt … 2.3 road) × the wheel's grip × 0.66 across, so road ≈ 1.4 and rough ground ≈ 1.0, and the original's surface types still matter; × 0.8 along (drive and brake): the original's traction is the surface's whole grip times g, and with this its launch matches, 0–26 u/s in 4.4 s against the original's 4.5 for the Piranha on m05 |
| tyre curve | magic formula sin(C·atan(B·α)), B 13 (rear ×1.35, so it understeers at the limit), C 1.45: peak near 8°, 80% when sliding; slip angle measured against at least 2.5 u/s, so a car at rest is held rather than jittered; the lateral force first, drive and brake take what the friction ellipse leaves (at least a third) |
| rolling | the surface's rolling × the wheel's, on each wheel's load, as the original's (the first version had half; `loose` keeps that) |
| drag | the original's: VDFC drag · 0.1 · v² per unit mass |
| blown tyre | 45% grip on that wheel |

Every number lives in `ModernCar.Tuning`. `I76_MODERN_TUNE="GripScale=0.7,Frequency=2"`
overrides any of them for a run.

**Input.** The original's keys and ramps (`KeyRamp`), at the body's step.
The original adds to the throttle once a frame, so it builds faster at a
higher Simulation Rate; here it is by time, the original's per second at the
session's tick (0.4/tick·(1 − hold/3) up, 0.5/tick·(1 − hold/3) down: 8.0 and
10.0 a second at 20 Hz, 24.1 and 30.1 at 60; held 3 s at most, caps ±0.9999).
The original model keeps its per-tick ramp. The pad's axes pass straight through, also every
step. Throttle builds over the hold, steering is √(t/3) of full lock, and Tab
flips the gear for reverse. Full lock is 0.6 rad at a standstill. At speed it
falls to what turns the car at about 16 u/s² plus the front tyres' slip
(wheelbase·16/v² + 0.11), so the keyboard's full lock asks for the limit and
not far past it. The wheels turn at most 3.2 rad/s. Throttle < 0 brakes,
drive state 0 brakes and 2 cuts the drive, as the step reads them. The
handbrake slides the rear wheels.

**Rollover.** Nothing stops it: the car rolls on its hull. On its side or roof,
with fewer than three wheels down and under 3 u/s for 1.2 s, it rights
itself with a torque about the axis that brings it upright and a lift of 0.6 g.
If it is still stuck after 6 s, it is put back on its wheels where it is.
This replaces the original's settle test, which wrecks an upturned car
unless "Arcade Phys." is on.

### 2.1 The handling assist

Driven by its tyres alone, the body handled like a modern car, not like the
original: it turned half as tightly at full lock, answered the keys in 0.3 s
instead of within a frame, and it flew off crests and bumps that the original
car rolls over. So by default an assist puts the original's handling on the
body (`ModernCar.Assist`). The body is still a Jolt rigid body with its
suspension, its hull, its contacts and its tumbles. The original's grounded
model is kinematic, so the assist sets velocities as a constraint would; the
solver still answers contacts after it.

- **Yaw.** On its wheels, the yaw rate is set to the original's commanded rate
  (steer · speed · SteerGain, capped at the lateral limit over
  the speed), within half the original's tick (§2.2). The front wheels turn to the matching angle;
  the tyres' forces act along the heading, as the original's drive and brake do.
- **The pivot.** The original turns the car about a pivot on its rear axle
  (the rear wheels' z), and the velocity it keeps along the heading
  is that point's. The rules below act on the pivot's velocity, and the yaw
  turns the body about it, so the car's nose sweeps out the original's wider
  circle (R² = (v/ω)² + pivot²).
- **Velocity.** Under the load-based cap, the slip goes and the whole speed
  points along the heading at once, as the original's `fwd · signedSpeed`. Past the
  cap, only the original's blend of the turn is applied (1 at the cap, 0 at
  twice it), so slip builds and slows the car until it is under the cap again
  and grips: the original's full-lock drift, a cycle of about a second. Past twice the cap the car
  slides on the original's sliding model: yaw eased toward steer · π at
  YawRateLimit, side friction against the slip. It also slides on the
  handbrake, or when knocked past 7 u/s of slip while gripping. The slide ends under 3 u/s
  of slip, as in the original.
- **Drive.** The original's engine force acts along the heading at the centre
  of mass, up to the original's traction (the rear wheels' grip on a quarter of
  the load each, times the surface's grip · g). It is not routed through
  whichever rear tyre a bump has just unloaded, which bogged the car down on
  rough ground.
- **Staying on the ground.** The wheel rays reach 0.5 past full droop. While
  two wheels are that close, any velocity away from the ground's plane past
  1.5 u/s is turned back into the plane at the same speed: the original keeps
  its velocity in that plane until the ground falls more than 0.5 below its
  path in a frame. The 1.5 u/s is the springs' rebound, and only up to the
  ride height: once the near wheels' mean travel is longer than at rest, no
  speed away from the plane is allowed. Without that limit the car rose off
  every crest at 1.5 u/s until its wheels were out of reach, and flew where
  the original's car follows the ground (T04's race loop, §2.1's table). The plane runs through the wheels' ground points, not the
  heightfield triangles' normals, which tip by tens of degrees from wheel to
  wheel on rough ground. Roll and pitch rates are damped (6/s). Speeds away
  from the ground above 0.5/tick u/s are let go (10 u/s at 20 Hz, 30 at 60),
  so a ramp's end or a ledge still flies.
- **Landing.** For 0.4 s after a flight of more than 0.25 s, all rebound is
  held and the rebound damping is tripled, so a big landing does not bounce
  the car off again. In the air, roll and pitch rates are damped at 2/s.
- **Body.** Soft anti-roll (0.15 of the spring rate), and a roll spring
  toward a lean out of the turn (0.0096 rad per u/s², at most 0.0975). The
  springs push along the body's up less its lean, so the lean does not push
  the car out of the turn. The hull's floor is the box between the axles,
  raised 0.15, so the overhangs meet a rise ahead at the waist rather than
  digging in.
- **Off its wheels** (more than 40° to the ground, on its side or roof) the
  assist lets go. The car falls back or rights itself as it does without the
  assist: 3.8 s from upside down, against 3.2 s for `loose`.

`I76_MODERN_TUNE=loose` is the first version, tyres and suspension alone
(Assist 0, Stick 0, anti-roll 1.1, half rolling, no rate scaling). Each
field above can be set by name as well (`Assist`, `YawResponse`,
`YawAccelMax`, `SlideEnter`, `SlideExit`, `SlideFriction`, `SlipResponse`, `Pivot`, `Tick`, `DriveScale`, `StickReach`,
`LiftAllowance`, `StickBreak`, `TiltDamp`, `AirTiltDamp`, `Settle`,
`SettleRebound`, `LeanPerAccel`, `LeanMax`, `LeanSpring`, `HullChamfer`).

### 2.2 Rate

The original's model is frame-coupled (docs/frame-rate.md §4–5), so "the
original's handling" depends on the Simulation Rate. Modern Physics now follows
it: `ModernPhysics.Rate` is the session's rate (a checkpoint's retry keeps the
checkpoint's, F7 mid-mission keeps the session's), and `ModernCar.ApplyRate`
sets the tuning from `I76.Sim.Session.ModernRateTuning` before any
`I76_MODERN_TUNE` override. Jolt still steps at 120 Hz.

What the measurements below show of the original against the tick:

- **Unchanged by the tick:** the grip circles (R and yaw within 0.5% from
  20 to 60 Hz), the body roll, and the slide onset (28–31 u/s² at 25 u/s, the
  same formula at every rate). Full lock at speed passes the load-based cap but
  not twice it, so the Piranha never reaches the sliding model in steady
  driving; its full-lock "drift" is the blend's cycle: slip builds, the car
  slows under the cap, grips at once and speeds up again.
- **Changed by the tick:** the throttle ramp (per tick), when a key is
  answered (the keys are sampled once a tick), acceleration (0–26 u/s 4.37 s at
  20 Hz, 4.63 s at 60), the stopping distance from 25 u/s (31.5 m, 27.4 m), the
  drifts (R 9.1 at 20 Hz, 7.2 at 60, at 20 u/s full lock), the speed a turn
  gains, the ground's hold, and the m01 descent (54.9 u/s after 12 s at 20 Hz,
  47.9 at 60).

Where the dependence follows from how the original steps, it is a rule of the
tick, not a fit:

- **Keys:** the throttle ramp is the original's per tick at the session's
  rate, per second (`KeyRamp.UpPerSecond`: 0.4/tick, 0.5/tick down); steering
  is √(t/3) of full lock by the time held, as the original's.
- **The yaw's answer:** `YawResponse` = tick/2 − 1/240 s (at least 4 ms). The
  original answers a key within the tick that samples it, half a tick later on
  average; the body samples every 120 Hz step, half a step on average.
- **The ground's hold:** `StickBreak` = 0.5/tick: the original lets go when the
  ground falls 0.5 below its path within a tick.
- **The turn's speed gain:** each tick the original adds the centripetal
  ω·v·tick across the velocity, so the speed grows by ½(ω·tick)² a tick,
  ½·ω²·tick·v a second (with the blend's share of ω past the cap). At 20 Hz
  that is 9%/s at full lock and 10 u/s, which the original's driver feels as a
  car that runs on through a turn; it sets the drift's speed and radius at each
  rate. The assist adds it (`Tuning.Tick`).
- **The drive's step:** the original adds drive·tick at the tick's starting
  speed; for a force ∝ 1/v that gains ½·tick·a²/v a second (under 1%).

The rest is fitted per rate, from the targets as smooth functions of the rate
(fitted to the six measurements), through the body's measured response to the
parameter:

| rate (Hz) | 20 | 25 | 28 | 30 | 40 | 60 |
|---|---|---|---|---|---|---|
| target 0–26 u/s (s), quadratic in the rate | 4.37 | 4.39 | 4.40 | 4.41 | 4.47 | 4.63 |
| `DriveScale` | 0.968 | 0.962 | 0.957 | 0.954 | 0.939 | 0.900 |
| target stop from 25 u/s (u), linear in the rate | 31.2 | 30.7 | 30.4 | 30.2 | 29.3 | 27.3 |
| `BrakeScale` (× 25 760 N) | 0.805 | 0.813 | 0.817 | 0.821 | 0.838 | 0.877 |

None of the slide parameters needed a rate: the sliding model is reached only
by the handbrake, a landing or a knock, and its numbers are the original's
formulas. Fitting showed differences of the first fit from the original at
every rate, and these were fixed in the model rather than fitted: the pivot on
the rear axle (§2.1), slip taken out at once while gripping (it was over
0.01 s, which ran the full-lock drift 20% tight and 10% slow), the knock rule
only while gripping (in a long drift the original carries slip past 7 u/s,
which put the car into the sliding model and spun it), the tyres' forces along
the heading (a steered front wheel's brake pushed the car across), the
original's traction and rolling resistance for the drive (the first fit
reached 26 u/s 12–18% early), and the lean (0.0096 rad per u/s², at most 0.0975).

Tests: `KeyRampTests` (the ramp per second at each rate, equal to the
per-tick ramp at that rate's tick, steering by time) and
`ModernRateTuningTests` (one row per offered rate, the lookup, the rule
values, the fitted values monotone in the rate). The Jolt car cannot run
without Godot, so its numbers are measured with `DriveTest` and given below.

### 2.3 Measured

All runs use the Piranha, scripted with `DriveTest` (`I76_TEST_CRUISE`,
`_STEER`, `_HEADING`, `_BRAKE`, `I76_TELEMETRY`), rendered at 120 fps. The
original model is the same build with the option off; it reproduces the real
game's dependence on the frame rate (docs/frame-rate.md §5.5). Circles are on
m05's flat ground at 3300,48950: cruise control to the speed, the steer held
from 8 s to 18 s (ticks converted per rate), the averages over the last 5 s.
R is the path's radius at the car's position, yaw the heading's rate (rad/s),
slip the sideways speed at the original's pivot (u/s; the body's centre of mass
is 1.33 ahead of it), roll the body roll. "Key to yaw" is the mean time from a
key press to 63% of the yaw: half a tick for the original; for the body half a
step plus the heading's lag measured at every physics step
(`I76_TELEMETRY=phys`). Throttle to full is the mean from the key press. The
slide onset is the lateral acceleration of the last and first steps of a steer
staircase at 25 u/s (0.1 a step) without and with slip over 1.5 u/s. Braking:
full brake from cruise at 25 u/s. The descent: m01's start, full throttle, a
70 m drop from about 5 s. "Before" is the first fit (the code before this rate
work, the same at every rate), "after" the rate's tuning.

| **20 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.12 | 1.75 | 2.13 |
| 0–26 u/s (s) | 4.37 | 3.84 | 4.38 |
| speed after 10 s (u/s) | 40.2 | 42.3 | 40.2 |
| stop from 25 u/s (u) | 31.5 | 27.8 | 31.6 |
| m01 descent, speed after 12 s | 54.9 | 58.3 | 55.8 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.7, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.89 | R 5.2, yaw 1.87 | R 5.4, yaw 1.85 |
| 20 u/s, half lock | R 25.7, yaw 0.78 | R 25.1, yaw 0.79 | R 25.3, yaw 0.79 |
| 30 u/s, half lock | R 57.5, yaw 0.52 | R 56.7, yaw 0.53 | R 56.9, yaw 0.52 |
| 20 u/s, full lock (drift) | R 9.1, 16.0 u/s, slip 4.2 | R 6.9, 13.9 u/s | R 9.0, 15.6 u/s, slip 3.8 |
| 30 u/s, full lock (drift) | R 8.8, 15.7 u/s, slip 3.6 | R 7.7, 13.1 u/s | R 8.0, 14.9 u/s, slip 3.8 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 25 | ≈14 | 26 |
| throttle key to full, mean (s) | 0.12 | 0.10 | 0.14 |
| slide onset at 25 u/s (lateral u/s²) | 28.1–31.1 | 28.1–31.5 | 28.1–31.4 |

| **25 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.12 | 1.74 | 2.14 |
| 0–26 u/s (s) | 4.39 | 3.84 | 4.39 |
| speed after 10 s (u/s) | 40.1 | 42.3 | 40.1 |
| stop from 25 u/s (u) | 30.9 | 27.3 | 30.9 |
| m01 descent, speed after 12 s | 55.6 | 58.3 | 55.7 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.7, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 5.2, yaw 1.87 | R 5.4, yaw 1.84 |
| 20 u/s, half lock | R 25.6, yaw 0.78 | R 25.1, yaw 0.79 | R 25.2, yaw 0.79 |
| 30 u/s, half lock | R 57.4, yaw 0.52 | R 56.7, yaw 0.53 | R 56.8, yaw 0.53 |
| 20 u/s, full lock (drift) | R 8.8, 15.7 u/s, slip 4.1 | R 6.9, 13.9 u/s | R 8.6, 15.2 u/s, slip 3.8 |
| 30 u/s, full lock (drift) | R 8.8, 15.6 u/s, slip 3.4 | R 7.7, 13.1 u/s | R 8.0, 14.9 u/s, slip 3.4 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 20 | ≈14 | 21 |
| throttle key to full, mean (s) | 0.10 | 0.10 | 0.11 |
| slide onset at 25 u/s (lateral u/s²) | 28.1–31.1 | 28.2–31.7 | 28.2–31.5 |

| **28 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.13 | 1.73 | 2.14 |
| 0–26 u/s (s) | 4.40 | 3.83 | 4.40 |
| speed after 10 s (u/s) | 40.0 | 42.3 | 40.0 |
| stop from 25 u/s (u) | 30.6 | 26.9 | 30.4 |
| m01 descent, speed after 12 s | 53.6 | 58.4 | 55.7 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.7, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 5.2, yaw 1.87 | R 5.4, yaw 1.83 |
| 20 u/s, half lock | R 25.6, yaw 0.78 | R 25.1, yaw 0.79 | R 25.2, yaw 0.79 |
| 30 u/s, half lock | R 57.4, yaw 0.52 | R 56.7, yaw 0.53 | R 56.7, yaw 0.53 |
| 20 u/s, full lock (drift) | R 8.4, 15.4 u/s, slip 4.1 | R 6.9, 13.9 u/s | R 8.3, 15.0 u/s, slip 3.8 |
| 30 u/s, full lock (drift) | R 8.7, 15.6 u/s, slip 3.3 | R 7.7, 13.1 u/s | R 8.1, 14.9 u/s, slip 3.3 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 18 | ≈14 | 20 |
| throttle key to full, mean (s) | 0.09 | 0.10 | 0.10 |
| slide onset at 25 u/s (lateral u/s²) | 28.1–31.1 | 28.4–31.3 | 28.4–31.1 |

| **30 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.13 | 1.73 | 2.14 |
| 0–26 u/s (s) | 4.41 | 3.83 | 4.42 |
| speed after 10 s (u/s) | 39.9 | 42.3 | 39.9 |
| stop from 25 u/s (u) | 29.5 | 26.1 | 29.4 |
| m01 descent, speed after 12 s | 52.1 | 58.4 | 55.6 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.6, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 5.2, yaw 1.87 | R 5.4, yaw 1.83 |
| 20 u/s, half lock | R 25.6, yaw 0.78 | R 25.1, yaw 0.79 | R 25.1, yaw 0.79 |
| 30 u/s, half lock | R 57.4, yaw 0.52 | R 56.7, yaw 0.53 | R 56.7, yaw 0.53 |
| 20 u/s, full lock (drift) | R 8.2, 15.3 u/s, slip 4.0 | R 6.9, 13.9 u/s | R 8.1, 14.9 u/s, slip 3.8 |
| 30 u/s, full lock (drift) | R 8.7, 15.5 u/s, slip 3.3 | R 7.7, 13.1 u/s | R 8.1, 14.9 u/s, slip 3.2 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 17 | ≈14 | 18 |
| throttle key to full, mean (s) | 0.08 | 0.10 | 0.10 |
| slide onset at 25 u/s (lateral u/s²) | 28.1–31.1 | 28.1–31.6 | 28.2–31.4 |

| **40 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.14 | 1.72 | 2.15 |
| 0–26 u/s (s) | 4.47 | 3.82 | 4.47 |
| speed after 10 s (u/s) | 39.7 | 42.3 | 39.6 |
| stop from 25 u/s (u) | 29.3 | 26.5 | 29.3 |
| m01 descent, speed after 12 s | 52.5 | 58.4 | 55.4 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.6, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 5.2, yaw 1.87 | R 5.4, yaw 1.83 |
| 20 u/s, half lock | R 25.6, yaw 0.78 | R 25.1, yaw 0.79 | R 25.1, yaw 0.79 |
| 30 u/s, half lock | R 57.3, yaw 0.52 | R 56.7, yaw 0.53 | R 56.5, yaw 0.53 |
| 20 u/s, full lock (drift) | R 7.3, 15.1 u/s, slip 3.5 | R 6.9, 13.9 u/s | R 7.2, 14.5 u/s, slip 3.5 |
| 30 u/s, full lock (drift) | R 8.7, 15.4 u/s, slip 3.2 | R 7.7, 13.1 u/s | R 8.3, 14.9 u/s, slip 3.0 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 12 | ≈14 | 15 |
| throttle key to full, mean (s) | 0.06 | 0.10 | 0.07 |
| slide onset at 25 u/s (lateral u/s²) | 28.1–31.1 | 28.1–31.6 | 28.2–31.4 |

| **60 Hz** | original | before | after |
|---|---|---|---|
| 0–60 km/h (s) | 2.21 | 1.71 | 2.20 |
| 0–26 u/s (s) | 4.63 | 3.81 | 4.62 |
| speed after 10 s (u/s) | 39.0 | 42.4 | 38.8 |
| stop from 25 u/s (u) | 27.4 | 25.7 | 27.4 |
| m01 descent, speed after 12 s | 47.9 | 58.4 | 54.7 |
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.5, yaw 0.94 | R 10.6, yaw 0.93 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 5.2, yaw 1.87 | R 5.4, yaw 1.82 |
| 20 u/s, half lock | R 25.5, yaw 0.78 | R 25.1, yaw 0.79 | R 25.0, yaw 0.79 |
| 30 u/s, half lock | R 57.3, yaw 0.52 | R 56.7, yaw 0.53 | R 56.4, yaw 0.53 |
| 20 u/s, full lock (drift) | R 7.2, 15.0 u/s, slip 3.3 | R 6.9, 13.9 u/s | R 6.6, 14.3 u/s, slip 3.0 |
| 30 u/s, full lock (drift) | R 8.6, 15.4 u/s, slip 3.3 | R 7.1, 13.1 u/s | R 8.0, 14.6 u/s, slip 2.9 |
| body roll, 20 u/s half lock (°) | 6.3 | 5.9 | 6.4 |
| key to yaw, mean (ms) | 8 | ≈14 | 15 |
| throttle key to full, mean (s) | 0.04 | 0.10 | 0.05 |
| slide onset at 25 u/s (lateral u/s²) | 28.2–31.3 | 28.1–31.6 | 28.2–31.4 |

A second car for sanity, the Goon (`vgoonf.vcf`), with the Piranha's fitted
table (its pivot slip is left out):

| Goon | original 20 Hz | after 20 Hz | original 60 Hz | after 60 Hz |
|---|---|---|---|---|
| 0–60 km/h (s) | 4.74 | 4.81 | 5.01 | 5.19 |
| 0–26 u/s (s) | 11.66 | 11.72 | 12.32 | 12.76 |
| stop from 25 u/s (u) | 40.7 | 42.4 | 32.9 | 34.9 |
| 10 u/s, full lock | R 3.4, yaw 2.49 | R 3.3, 2.50 | R 3.4, 2.49 | R 3.4, 2.47 |
| 20 u/s, half lock | R 25.7, yaw 0.78 | R 25.2, 0.79 | R 25.2, 0.79 | R 24.7, 0.80 |
| 20 u/s, full lock | R 3.3, 8.9 u/s | R 3.3, 8.6 u/s | R 3.3, 8.7 u/s | R 3.3, 8.5 u/s |
| body roll, 20 u/s half lock (°) | 6.9 | 5.9 | 6.9 | 5.9 |

At every rate the body now matches the original within 1% on acceleration
and braking, 1–3% on the grip circles, 1–9% on the drifts' radius and 3–5% on
their speed, and within 7% on roll but for 10 u/s at half lock (5.2° against
5.8°). Where it cannot:

- **The m01 descent at 30–60 Hz.** Within 4% at 20–28 Hz, but 5–7% fast at
  30–40 and 14% at 60. The original loses speed over the descent's crest and
  dip at small ticks (its terrain pin and re-landing each substep), not in a
  way that follows from the tick by a rule, and the body rides the ground the
  same way at every rate. A drag fitted to this one descent would change every
  hill climb, so it is left.
- **Key to yaw at 40 and 60 Hz:** about 15 ms against the original's 12 and
  8: the body's own step and yaw rate limit put a floor near 10 ms on its
  answer.
- **Throttle to full** is 0.01–0.02 s later than the original's: the
  original's three steps overshoot (1.19 before the cap), the ramp by time
  does not.
- **The Goon** brakes 4–6% long and leans 1° less (the lean's shape is one for
  every car, the original's tilt is the car's own), and at 60 Hz reaches
  26 u/s 4% late: the drive and brake were fitted on the Piranha.

The first fit, at the 0.036 s tick, against the tyres alone (`loose`):

| | original | before (`loose`) | assist |
|---|---|---|---|
| 10 u/s, half lock | R 10.8, yaw 0.93 | R 10.3, 0.96 | R 10.5, 0.94 |
| 10 u/s, full lock | R 5.5, yaw 1.88 | R 10.4, 0.91 | R 5.2, 1.87 |
| 20 u/s, half lock | R 25.6, yaw 0.78 | R 35.3, 0.55 | R 25.1, 0.79 |
| 20 u/s, full lock | drifts: R 7.8, yaw 2.06, slows to 15.2, slip 3.8 | R 32.2, 0.59, 19.1 | drifts: R 6.1, 2.25, 13.9, slip 3.6 |
| 30 u/s, half lock | R 57.4, yaw 0.52 | R 72.7, 0.40 | R 56.7, 0.53 |
| 30 u/s, full lock | drifts: R 9.9, yaw 2.00, slows to 15.7 | R 70.7, 0.40, 28.5 | drifts: R 8.4, 2.14, 14.7 |
| response, release | within 1 frame | 0.14–0.25 s | 1–2 frames (0.04–0.07 s) |
| body roll in a turn | 6.3° | 1.5–1.8° | 5.4–6.0° |
| braking from 25 | 2.34 s, 29.6 u | 2.05 s, 26.2 u | 2.05 s, 26.1 u |

Straight runs at 30 u/s with the heading held, over the roughest ground near
each mission's start (found with `i76 bumps MISSION`), with the first fit at
28 Hz (not measured again since §2.2). The table gives the
time airborne and the flights longer than 0.25 s; the loose car's long
flights are mostly a rollover.

| route | original | before (`loose`) | assist |
|---|---|---|---|
| t01 rough slope (2913,35285, 210°) | 1.6 s, 2 flights, max roll 29° | 7.3 s, rolled over (88°) | 1.8 s, 2 flights, 31° |
| a01 crests (1013,49951, 330°) | 1.6 s, 1 flight | 7.1 s, 4 flights, rolled over | 0.9 s, 2 flights |
| t08 hillside (3236,47503, 60°) | 0.9 s, 1 flight | 2.4 s, rolled over (87°) | 3.0 s, 4 flights, 56° |
| m05 ramp pair | 3.2 s flight, peak 15.9 | 2.8 s, 16.1 | 2.2 s, 16.4 |
| m05 14 m ledge at 25 | 2.7 s (hits the lip, 2 flights) | 2.3 s, bounces (2 flights) | 1.7 s, 1 flight, no bounce |

Still different:

- t08's hillside throws the assisted car more than the original. A 4 m
  terrain step there is a wall to the hull, while the original rides over it.
- The ramp flight is shorter. The car leaves the ramp at much the same speed
  and peak height, but noses down sooner.
- The m01 descent at 30–60 Hz, and the others listed in §2.3.

**The ground's hold at 20 Hz (October 2026).** Reported: the car takes off
easily on T04, where the original's sticks to the road. The lift allowance
was applied whatever the suspension's length, so over a crest the body rose
off the ground at 1.5 u/s until its wheels were out of reach. It now holds
only below the ride height (above). The share of the time airborne, the car
alone at the default 20 Hz: T04 round its race loop (the script's path 13)
by a fixed rule (`I76_TEST_ROUTE`, `i76
airtime`), the others straight at 30 u/s for 25 s as above (t01, a01 and
t08 from the same starts, m01, m05 and m08 from `i76 bumps`). The original
is the real game at a fixed 50 ms frame (the
reference clock's 49.8/50.3 ms sequence), at 36 ms where marked.

| route | original | the port (20 Hz) | before | after |
|---|---|---|---|---|
| T04 loop at 30 u/s, 121 s | 0.0% (0.2% at 36 ms) | 0.0% | 5.6%, 10 flights, to 1.8 s and 2.5 m | 0.0% |
| T04 loop at 40 u/s | 0.0% (36 ms) | 0.0% | 14.0%, 13 flights, to 2.0 s and 5.5 m | 0.0% |
| t08 hillside | 11.7%, 3 flights | 16.9% | 19.5%, 15 | 10.8%, 14 (shorter) |
| t01 rough slope | (1.6 s, 2 flights at 36 ms) | 11.5% | 9.9%, 19 | 2.9%, 3 |
| a01 crests | (1.6 s, 1 flight at 36 ms) | 27.2% | 10.4%, 7 | 10.8%, 5 (its long flight kept) |
| m01 / m05 / m08 | — | — | 2.5% / 17.6% / 8.1% | 0.0% / 7.6% / 8.9% |

m08's "after" is mostly one throw off a rock the car now reaches on the
ground (before, it flew over the crest in front of it).

## 3. Scope of the first version

- the player's car only, per game (the flag or setting, F7 live);
- AI cars, scripts and cut-scenes on the original model;
- terrain, scenery, structures and their destruction, signs, car contacts,
  terrain and scenery damage, landing, the fall kill, rough terrain, the
  death throw and mines;
- weapons, radar, HUD, sounds and the cockpit read the Vehicle as before.

## 4. What was checked

Captures (tools/shot.sh, 30 fps, keys scripted with I76_GAME_KEYS, the body's
state logged every 5 sim frames with `I76_DEBUG_MODERN=1`) on m05 and t01:

- **accelerate, turn, hit a wall**: 0–25 u/s, a left turn with the body
  rolling, then into the bunker's wall at 25 u/s. The car stops dead and
  takes 14% off its front zone. Through the bunker's channel, a straight run
  meets nothing;
- **acceleration** against the original model from the same spot: 26.6 u/s
  after 121 frames against 26.4 after 125;
- **cornering**: a 90° left at 12–17 u/s, the inner rear wheel nearly
  unloaded and the car steady; full throttle through a turn no longer spins
  it;
- **braking**: 21 → 0 u/s in about 1.8 s;
- **handbrake**: a J-turn at 18 u/s; **reverse** (Tab) at up to 8 u/s,
  steering;
- **jumps**: over a crest, and off m05's ramp pair at 30 u/s. The car falls
  short of the second ramp and tumbles, as the original model does from the
  same run-up;
- **rollover**: started upturned 1.5 above the ground, the car lands on its
  roof and rolls itself back onto its wheels in about 3 s;
- **structures**: drives over m05's bridge, 10 above the riverbed, on its
  polygons;
- **cars**: a parked 2885 kg car rammed at 21 u/s. The body drops to 5 u/s and
  the other car is shoved down the road. A bus (--ai chase) rams the parked
  player at 13 u/s and spins it round. Weapons, rounds and the HUD all
  carry on;
- **switching**: F7 mid-run hands over both ways at 18 and 26 u/s with
  no jump in speed; t01's opening cut-scene runs on the original model;
- the sim tests (trace equivalence included) pass unchanged.

## 5. Known gaps and next steps


- the AI's throttle and steering controllers were tuned on the original
  model, so a cut-scene that drove the player through them would differ, and
  so cut-scenes stay on the original model;
- no wheel-spin state (the drive is capped by the grip, a traction
  control); no gearbox of its own beyond the original's automatic;
- drive-on structure surfaces are polygons in Jolt; the sim's ±3 height
  window for choosing a surface does not apply;
- AI cars are not bodies: a body resting against a parked sim car is only
  pushed by the pair test's impulse, which needs the cars to be closing, and
  the pair test samples eight points at the boxes' mid-height, so a car can
  ride a little into another before it answers;
- the original's settle test (an upturned car is wrecked unless Arcade Phys.
  is on) does not run; the car rights itself instead;
- skid marks follow the body's sliding flag (SkidMarks);
  no tyre smoke from its slip yet;
- the HUD's speed is the body's, but the tacho and the shift points are the
  original's automatic box on that speed, so the engine note follows the
  original's gears;
- next: AI cars on the same model (each a body; the pair test then retires
  for body pairs and Jolt answers), a gamepad's analogue steering and
  throttle, wheel spin and a clutch-less manual box, dust.
