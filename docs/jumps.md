# Jumps: how the car flies and lands

How the original's vehicle model (the default; src/I76.Sim/Vehicles/VehicleStep.cs) takes a car off a ramp, through
the air and back onto the ground, and what that means for landing flat. T05's canyon jump is the worked example. Every
number is from the code or from scripted jumps on T05 through I76.Sim (how they were run is at the end).

## The short answer for T05

- **Leave the lip at 49 to 56 m/s (about 110 to 125 mph).** That carries the car over the canyon onto the flat ground
  past the far landing ramp. Under about 46 m/s it hits the canyon's far wall; 47-48 m/s puts it on the landing ramp's
  face. Holding the throttle up the ramp or letting it go makes no difference; braking on the ramp makes things worse.
- **Nothing you press in the air changes the flight.** Throttle, brake and steering do nothing until a wheel is down.
- **Whether it lands flat is decided at the lip, not by you.** At Simulation Rate 20 (the default) about 4 take-offs in
  10 are clean and land within 10 degrees of flat; about 2 in 10 come down 40 degrees nose-low on their wheels; about 4
  in 10 come down nose-first, roll onto the roof and are destroyed (Arcade Phys. turns them back over instead).
- **Landing flat does not mean landing softly.** The landing damage counts only the car's speed into the ground,
  whatever its angle: 6-8 % of the Piranha's health on T05 (section 5). What a flat landing saves you from is the roll.

The chart of 504 scripted jumps (speed at the lip against the nose's angle at touchdown, the flat band shaded) is with
the branch's evidence; the tables below carry the same numbers.

## 1. Leaving the ramp

On the ground the car's velocity is kept in the plane of the ground under it every step (the grounded model turns it
into that plane after integrating), so on a ramp the car travels along the ramp's face. T05's take-off ramp rises at
16.3 degrees (its normal is (0.28, 0.96)); the car leaves along that line.

Each step begins with a ballistic test: the car is moved by its velocity and gravity alone, and if that puts it more
than 0.5 m above the ground below (or still rising above its last clearance once airborne) it is airborne. Past T05's
lip the ground falls 6.9 m to the plateau and then 26 m into the canyon, so the car is airborne on the first step whose
end lies past the lip.

Climbing the ramp costs speed twice: at the foot, where the velocity is turned into the ramp's plane while the body
pitches up to it, and on the climb itself (6.9 m). From the speed on the flat run-up to the speed at the lip, measured:

| run-up (m/s) | 50 | 54 | 56 | 58 | 60 |
|---|---|---|---|---|---|
| lip, Simulation Rate 20 | 43.9-46.2 | 47.6-50.4 | 48.7-51.8 | 52.8-54.2 | 53.9-55.8 |
| lip, Simulation Rate 30 | 43.8-45.6 | 47.4-49.3 | 49.2-51.2 | 51.0-53.1 | 52.9-55.0 |
| lip, Simulation Rate 60 | 39.4-39.5 | 40.7-42.8 | 42.4-44.4 | 44.0-46.0 | 45.5-47.7 |

The spread at one run-up speed is where the steps fall on the ramp's foot. At 60 the body takes more steps to pitch up
to the ramp and the velocity is turned into its plane on each of them, so the foot costs 11 m/s instead of 4.

### The take-off pitch rate: the front wheels' last push

The body's pitch on the ground comes from its wheels (the attitude step): each wheel still above the ground after the
body is lifted onto its lowest wheel asks for a pitch of π rad/s (a front wheel nose-down, a rear wheel nose-up) and a
roll of π/2, and the pitch rate is eased toward the sum by a share of twice the step's length (2·dt) each step. So the
front wheels hanging over the lip while the rear ones are still on the ramp ask for 2π rad/s nose-down, and one step of
that gives:

| what the last ground step saw | nose-down rate at take-off | Simulation Rate |
|---|---|---|
| all four wheels on the ramp | 0 (a clean lip) | any |
| both fronts over the lip, one rear reading a hair off | π · 2dt = 0.316 rad/s (18°/s) | 20 |
| both fronts over the lip | 2π · 2dt = 0.626 rad/s (36°/s) | 20 |
| both fronts over the lip | 2π · 2dt = 0.417 rad/s (24°/s) | 30 |
| fronts over the lip for one or two steps | 0.21 or 0.41 rad/s | 60 |

Which one you get depends on where the car is at the end of its last ground step: the Piranha's front axle is 1.36 m
ahead of the point the airborne test follows, and at 50 m/s a step at 20 is 2.5 m long, so the front wheels are past
the lip on that step a little over half the time. That is a fraction of a step, 1/20 of a second: no driver can choose
it. In 252 jumps at 20: clean 100, 18°/s 55, 36°/s 97.

The centre of mass plays no part: the model has none for pitch. The body turns about a pivot set by the wheels still in
contact, and the only "torque" is that per-wheel rate request.

## 2. In the air

Each step (VehicleStep's airborne branch):

- **Position and velocity:** gravity alone, `VehicleStep.Gravity` = −9.8 m/s², integrated exactly over the step
  (y += (vy − 4.9·dt)·dt). **No drag** in the air: the horizontal velocity never changes.
- **Pitch rate:** if the nose is falling slower than π/20 rad/s (0.157 rad/s, 9°/s) the rate is eased toward −π/20 by
  a share dt of the gap each step (about an e-fold a second); if it is already falling faster, it is **kept as it is**,
  undamped, for the whole flight. So a clean take-off ends up dropping its nose at 9°/s, and an 18°/s or 36°/s one keeps
  that rate to the ground.
- **Roll and yaw rates:** kept as they are.
- **The keys:** the airborne branch returns before the drive, the brake or the steering are read. Throttle, brake and
  steering do nothing in the air (the engine note follows the throttle). Checked: the same jump with the throttle
  held, the brake held, steering held or no key gives the same position, velocity and attitude at every step until
  touchdown (JumpFlightTests, and the probe's runs).
- **The body against the terrain:** every step the four corners of the body's box (at its mid-height) are swept
  against the bare terrain. A corner that meets it bounces the car (velocity reflected about the ground's normal, scaled
  by 1 − 0.75·cos²; an upward bounce over 1 m/s cut to a fifth), kicks its rates, and clamps them to ±π/2 in pitch, ±π
  in yaw and ±π/4 in roll. That is the nose striking first: the car is still airborne and tumbles on.
- **The 35 m kill:** an airborne car more than 35 m below the last ground it stood on is destroyed, mid-bounce or at
  touchdown. T05's canyon floor is 26 m below the lip, so this never applies there.

## 3. Why the nose drops

Three things, in order of size:

1. **The rate the lip gave it** (section 1): 0, 18°, 24° or 36° a second, kept undamped for the whole flight.
2. **The flight rule's floor:** even a clean take-off eases to 9°/s nose-down within a second or two.
3. **Time in the air:** about 3 to 3.8 s on T05.

So the nose angle at touchdown is, in degrees, with θ₀ the ramp's 16° and t the time in the air:

- clean lip: θ₀ − 9·(t − 1 + e^−t), about −5 to −10 at t = 3.4-3.8 s;
- a kept rate ω: θ₀ − ω·t, so −40 to −45 at 18°/s, −60 to −70 at 24°/s, −100 to −130 at 36°/s.

The "nose pitches about 90 degrees below about 110 mph" seen before: under about 46 m/s at the lip the car does not
reach the far side at all; its nose or underside strikes the canyon's far wall, the strike clamps the pitch rate at its
90°/s limit and the car tumbles into the canyon.

## 4. The flight and where it lands

T05's canyon, along the road (the car travels west):

| place | distance from the lip | height (m) |
|---|---|---|
| take-off lip | 0 | 26.9 (6.9 above the run-up) |
| canyon floor | 11 to 126 m | 1.0 |
| top of the far landing ramp | 132 m | 23.4 |
| foot of the landing ramp (it falls 16.3° toward you, your way) | 156 m | 16.4 |
| far flat ground | 156 to 236 m | 16.2-16.4 |

With v the speed at the lip and the ramp's 16.3°: vertical speed 0.28·v, horizontal 0.96·v, apex 0.004·v² m above the
lip (10.0 m at 50 m/s), and time to the far flat t = (0.28·v + √((0.28·v)² + 208)) / 9.8. At 50 m/s: 3.48 s, 167 m,
coming down at 20 m/s. To clear the landing ramp's top the lip speed must be at least 46.9 m/s; to land on its face,
46.9 to 48.0; faster lands on the far flat (the probe's cars reached it from 46.8; with a 36°/s take-off the dropping nose
can still catch the wall's top up to 48.4).

### A flat landing

The nose at touchdown should match the slope of what it lands on: 0 on the far flat, −16 on the landing ramp's face.
From section 3 the only take-off that does that is a clean one: −4 to −9 degrees on the far flat (49 to 58 m/s at the
lip), or 8 to 15 degrees nose-high relative to the landing ramp's face (47-48 m/s; the rear wheels touch first, which
is fine). A kept 18°/s rate lands 20-25 degrees nose-low on the landing ramp's face and 36-48 degrees nose-low on the
flat, wheels still first or a light nose strike, and survives. A 36°/s rate lands more than 90 degrees nose-low: the
body strikes, the car rolls and stops on its roof.

### Scripted jumps, Simulation Rate 20 (throttle held up the ramp)

| run-up m/s | lip m/s | nose-down rate at take-off, rad/s | in the air s | first touch | nose at first touch (ground's slope), degrees | health lost, 5 s on | outcome |
|---|---|---|---|---|---|---|---|
| 46 | 41.0 | 0.01 | 3.24 | canyon's far wall | +5 | 11.6 % | in the canyon, wheels down |
| 50 | 46.3 | 0.32 | 2.89 | canyon's far wall | −43 | 11.1 % | tumbled into the canyon |
| 54 | 47.6 | 0.32 | 3.14 | wheels, landing ramp | −42 (−16) | 6.2 % | wheels down |
| 56 | 51.8 | 0.01 | 3.49 | wheels, far flat | −6 (0) | 6.7 % | **flat**, wheels down |
| 58 | 52.8 | 0.02 | 3.59 | wheels, far flat | −8 (0) | 7.0 % | **flat**, wheels down |
| 56 | 49.5 | 0.32 | 3.39 | nose, far flat | −37 (0) | 5.9 % | wheels down |
| 56 | 48.7 | 0.63 | 3.24 | nose, foot of the ramp | −109 (−16) | 1.7 % | **destroyed** (on its roof) |
| 60 | 53.9 | 0.63 | 3.49 | nose, far flat | −117 (0) | 2.3 % | **destroyed** (on its roof) |

All 252 (throttle held, 126, and released, 126; six start points 0.4 m apart at each run-up speed 44-64):

| lip m/s | take-off | jumps | short (far wall) | landing ramp | far flat | nose at first touch, median (range) | destroyed |
|---|---|---|---|---|---|---|---|
| under 46 | clean | 32 | 32 | 0 | 0 | - | 0 |
| under 46 | 18°/s | 19 | 19 | 0 | 0 | - | 1 |
| under 46 | 36°/s | 42 | 42 | 0 | 0 | - | 18 |
| 46-48 | clean | 9 | 3 | 6 | 0 | +12 (+8 to +15) | 0 |
| 46-48 | 18°/s | 6 | 2 | 4 | 0 | −22 (−25 to −20) | 0 |
| 48-56 | clean | 44 | 0 | 3 | 41 | −6 (−9 to +11) | 0 |
| 48-56 | 18°/s | 23 | 0 | 1 | 22 | −40 (−45 to −27) | 0 |
| 48-56 | 36°/s | 40 | 4 | 6 | 30 | −114 (−122 to −89) | 33 |
| over 56 | clean | 15 | 0 | 0 | 15 | −6 (−10 to −1) | 0 |
| over 56 | 18°/s | 7 | 0 | 0 | 7 | −45 (−48 to −44) | 0 |
| over 56 | 36°/s | 6 | 0 | 0 | 6 | −127 (−131 to −126) | 6 |

(Nose angles are relative to the slope of the ground first touched; "short" rows have no meaningful angle.)

## 5. Landing: what hurts and what kills

- **Touchdown** is the step whose ballistic move ends 0.5 m or less above the ground (and no higher than the last
  clearance). The car is put on the ground there and flagged as just landed.
- **Landing damage:** `trunc(m · d² · K)`, with m the car's mass, K the damage model's `KOther` (1.000956e-4), and d the
  car's velocity along the normal of **the ground it last stood on**: the step tests the landing before it reads the
  ground under the car, so on T05 that is the take-off ramp's normal, not the landing surface's. It is applied under
  the car's centre. The car's angle does not enter it. On T05 d is about 33 m/s (20 down, plus the forward speed's share
  along the ramp's tilted normal): 206 for the 1846 kg Piranha, 6.7 % of its health; on the landing ramp's face 5.4 %.
  An impact never destroys a car by itself (no kill roll for collisions); it only wears the zones down.
- **A body strike** (a corner of the box meeting the terrain first, section 2) is a terrain impact by the speed into
  the ground at that corner: on T05 1.7 to 2.5 % for a nose-first touch on the flat.
- **On its roof:** once the pitch and roll rates are both under π/5 rad/s (0.628) and the car is square to the ground
  (|up·normal| > 0.8), a car lying upside down is destroyed. With Arcade Phys. (single player only) it is turned back
  onto its wheels instead: 42 jumps in the 54-60 m/s run-up band with it on, none destroyed, the 36°/s ones included.
- **The 35 m fall:** touching down more than 35 m below the last ground it stood on destroys the car.

## 6. What you can do

- **Speed:** aim for 49-56 m/s (110-125 mph) at the lip at Simulation Rate 20 and 30; that means about 54-60 m/s on the
  run-up. A full-throttle run down T05's hill reaches about 55-57 m/s at the ramp's foot in the original.
- **Throttle or not on the ramp:** no difference (held: 32 of 126 destroyed; released: 26 of 126, the spread of the
  lip lottery). It costs about 0.4 m/s at the lip to let go.
- **Do not brake on the ramp:** it costs 4-6 m/s at the lip (short more often), and the braking pitches the body on the
  ramp so the lip gives larger rates (up to 1.2 rad/s, and some nose-up ones): 49 of 126 destroyed.
- **In the air:** nothing helps. The keys come back the step a wheel is down.
- **Arcade Phys.:** the only thing that turns a nose-first landing into a survivable one (it rights the car).

## 7. Simulation Rate

The take-off rate comes in steps of 2π·2dt, so the tick changes it:

- **20 (default):** a lottery each jump: clean (flat) 40 %, 18°/s 22 %, 36°/s 38 %.
- **30:** 24°/s on 79 % of jumps and clean on 21 %; at 48-56 m/s none of 56 jumps was destroyed, but only the clean
  ones land flat; the rest land 60 degrees nose-low on the flat and come down on their wheels after a nose strike.
- **60:** never clean; 0.41 rad/s on most, 0.21 on some, and the foot costs so much speed that 56 m/s on the run-up
  leaves the lip at 42-44 and hits the far wall.

The original measured the same way (below) behaves like 30 here: its frames were 30-33 ms on the reference machine.

## 8. Modern Physics

Read from game/Scripts/Sim/Modern/ModernCar.cs and docs/modern-physics.md; not measured here (it needs the game's Jolt
world, not I76.Sim):

- The car is a rigid body with an inertia of its hull's box, its own raycast suspension, at 120 steps a second, under
  Godot's default 9.8 m/s² gravity, with the original's air drag (Drag · 0.1 · v² per unit mass) on in the air too.
- **The take-off pitch** comes from the body: the front tyres lose their support at the lip and gravity and the
  springs turn the body about the rear axle until those leave too. It does not come in steps of the tick.
- **In the air** the assist damps the pitch and roll rates at 2 per second (`AirTiltDamp`, an e-fold every 0.5 s), so
  whatever the lip gave dies away instead of being kept; the yaw rate is left alone. No drive or steering in the air
  either (the drive needs a tyre on the ground).
- **Touchdown** is any wheel on the ground after 0.25 s with none; the landing damage is the same formula with the
  velocity before and **the landing surface's** normal; a fall of more than 35 m destroys it.
- **On its roof** it is not destroyed: past 1.2 s nearly still on its side or roof it rolls itself over, and after 6 s
  stuck it is put back on its wheels.

## 9. Against the original

Reference: the retail 1.0 and Gold executables on an emulated Pentium 166 (86Box), simulation physics, the car placed
on T05's run-up at 56 m/s, full throttle (measurements kept with the i76-everywhere project; not rerun for this page):

| | original, retail (n 7) | original, Gold (n 7) | I26 at 20 (n 6) | I26 at 30 (n 6) |
|---|---|---|---|---|
| speed at the lip m/s | 51.6 ± 1.0 | 51.2 ± 1.3 | 48.7-51.8 | 49.2-51.2 |
| apex above the lip m | 10.7 ± 0.4 | 10.5 ± 0.5 | 10.1 (the clean one) | - |
| peak pitch, in the air and 3 s on, degrees | 53 ± 18 | 80 ± 8 | 16 to 90 | 65-68 (one 15) |
| destroyed | 0 | 3 | 4 | 0 |

The flight (lip speed, apex, landing place) matches; gravity and no drag are the original's to the measured precision
(9.80 m/s², ballistic ratio 1.000). The original's step was 30-33 ms on that machine, and its runs at longer steps
(40 ms and over, a frame-rate study on the same rig) turned the car over on most T05 jumps, as I26 does at 20.

## How the numbers were got

- A probe program on I76.Sim (outside the repo): T05 built without its script, the player's Piranha (vppirna1) alone,
  placed on the road 160 m east of the ramp's foot at a run-up speed on the ramp's centre line, the throttle held to that
  speed, then one of: throttle held, released, or the brake, from the foot on. Run-up 44-64 m/s in 1 m/s steps, six
  start points 0.4 m apart at each, at Simulation Rate 20 (throttle and release), 20 (brake), 30 and 60: 630 jumps;
  42 more with Arcade Phys. Each logs the speed and pitch rate on the take-off step, the time and place of the first
  touch (a body strike or the wheels), the nose and the ground's slope there, health, and the state 5 s later.
- tests/I76.Sim.Tests/JumpFlightTests.cs pins the in-air rules: gravity alone and no drag, the keys changing nothing,
  a nose-down rate over π/20 kept and a slower one eased toward it.
