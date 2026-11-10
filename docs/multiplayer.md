# Multiplayer: the network melee

The plan is a server-authoritative network melee: clients send only their
inputs, a server simulates everything, a client predicts only its own car.
This page covers what the simulation provides for that (phase Net-0), and
then the network itself: the server, the client and the wire (phase Net-1,
"Playing over the network"), and the client's prediction of its own car
("Prediction", protocol 13). The original's own network game, where each
machine owned its car and nothing was checked, is not reproduced here.

## The tick: `SimSession`

`I76.Sim.Session.SimSession` is the simulation's fixed step (its `SimRate`: 0.0498 s at the default 20 Hz) with
no Godot in it. `Tick(IReadOnlyList<CarInput> inputs)` does what
`SimDriver._Process` used to do for each step, in the same order:

1. Each driver's commands: the keys pressed since the last tick (weapon
   cycle and link, lights, poem, reverse, targets).
2. Each driver's controls: the key ramps or the analog
   position, the handbrake, and the latched trigger request. Nothing
   happens here while the car is dead or the script holds the camera.
3. `World.Frame`: the AI controllers, contacts, then the vehicle steps.
   `FrameStarting` and `FrameDone` are raised just before and just after
   it, for the hosts' test hooks.
4. Each driver's triggers, gated by the arms. A broken
   weapon's click goes into `Misfires`.
5. `Script.AfterFrame`, which runs the FSM or the melee and then the
   weapons. A mission without a script runs `Combat.Frame` instead.
6. Each driver's arms.

`inputs[i]` drives `Drivers[i]`, in the order the drivers were added. A
missing input means no keys. The AI and the mission script keep writing
their cars' controls from inside the tick, as before.

`SimDriver` is now only the Godot host. Each tick it reads `Controls` into
one `CarInput`, calls `Tick`, plays the sounds and interpolates the cars.
The CLI's `run` and the tests drive the same session headless.

## `CarInput`

Everything a human does to the simulation, in twelve bytes (`Write` / `Read`: buttons u32, commands u32, steer i16, throttle i16):

| Field | What |
|---|---|
| `Buttons` (held, sampled once a tick) | `ThrottleUp`, `ThrottleDown`, `SteerLeft`, `SteerRight`, `EBrake`, `Fire`, `Hardpoint1..5`, `Horn`, `Cockpit` (the view is in the car, so its arms are live), `RaiseRightArm` / `RaiseLeftArm` (the glance has turned to a window), `Analog` |
| `Commands` (one-off, applied at the start of the tick) | `Reverse`, `Lights`, `WeaponCycle`, `WeaponLink`, `TargetNearest`, `TargetNext`, `TargetFrontal`, `TargetReset`, `Poem` (the single player's car only), `Skip` (the script's isKeypress), `ShiftUp` / `ShiftDown` (the drive selector, 0..5), `StartEngine` (veh+0xec; nothing reads it yet), `Special1..3` (the jammer, nitro and the exhaust brake, `Specials.Activate`; the charges and the jammer's on-state ride in the own-car block), `RadarRange`, `RadarCamera`, `SelfDestruct`, `TargetPrev`, `RaiseDead` (a dead network player's RAISE_DEAD; the session ignores it, the host asks the melee) |
| `Steer`, `Throttle` | ±32767 analog positions, read only with `Analog` |

A host gathers presses between ticks in a `CommandQueue`. It sends one
command of each kind per tick, so two presses of the same key inside one
tick land in two ticks.

### What moved into the tick

- **Key ramps.** The ramps now run once a tick with dt = the tick (0.0498 s at 20 Hz). They used to
  run once a rendered frame on the render delta, so the throttle ramp
  (0.4·(1 − t/3) a step) reached full in three frames. That is now three
  ticks (0.15 s at 20 Hz, 0.11 s at 28) at any frame rate, which is about what the original did at
  its own frame rate. Steering depends only on hold time, so it is
  unchanged apart from being sampled on the tick. A capture driven by the
  same keys matches the old one exactly until the first steer, and stays
  within about a metre after that.
- **The driver's arms** (veh+0x400/+0x404) are now sim state:
  `Vehicle.Arms` (`CrewArms`). The cockpit used to step them on the render
  clock, and `SimDriver` asked it whether the hardpoints could fire.
  - Now the cockpit's glance only produces the `RaiseRightArm` /
    `RaiseLeftArm` bits.
  - The session steps the arms at the end of each tick: a tenth of a
    second a frame, the first frame at once.
  - A class-1 shot (tiers 1 and 2) starts the arm's shot frames from inside
    `Combat`.
  - Out of the cockpit the arms rest down every tick (already a deviation,
    "The arms out of the car").
  - The cockpit only draws the frames.

## Removing what a client could change

| What | Before | Now |
|---|---|---|
| Near/far path | the camera's position, which is local to one machine | `SimWorld.Far`: `FarPolicy.Player` (single player: near within 25 + the camera term, 873, of the player's car, never the camera; docs/deviations.md) or `FarPolicy.Humans`: near within that of any human's car, and far when no human is near |
| Cockpit arms gating the weapons | the cockpit's animation on the render clock | sim state driven by input bits (above) |
| Poetry decks | `new Random(Environment.TickCount)` inside the sim | a seed passed in: `MissionSetup.Build(..., seed)`. The game passes the clock, so single-player poems still come in any order |
| I76_SPAWN_AT, I76_BOOM | read wherever they were used | honoured only when `SessionOptions.DebugHooks` is set; `I76_BOOM` is now `SimSession.DebugBoom` |
| Modern physics | always available | `SessionOptions.ModernPhysics`; `EnableModern` refuses it when off |

`SessionOptions.SinglePlayer` allows everything, as before.
`SessionOptions.Network` allows no hooks, uses only the original physics,
and measures near and far from the humans.

`SimWorld.Humans` lists the human cars other than `World.Player`.
`IsHuman(v)` gives these cars the player's rules, as each original machine
gave them to its own car:
- the player's vehicle step;
- the weapon click;
- no wreck 10 s after death.

The difficulty's factor on the player's rounds and flame (Wimp ×2, Champion ×1, Badass ×0.75) is not one of them:
the original applies it outside a network session only, so every network game, melee or co-op, plays ×1 for
everyone (`Combat.NetworkGame`), whatever each player's Play Options say.

`Combat.UnlimitedAmmo` stays with `World.Player` alone. It is a play option.

## Stable ids

`Vehicle.NetId`, `Round.NetId` and `StaticObject.NetId` are allocated by
the session (`NetIds`). The kind is in the top byte (1 vehicle, 2 round,
3 structure), then a serial from 1 in creation order:
- Structures are numbered by their place in the static world when the
  session starts.
- Cars and rounds get an id at the start and end of every tick, in list
  order. A round that is spawned and spent within one tick never gets one.

Serials only grow, so an id is never reused within a session. A car renewed
in place (a regen pad, a respawn) keeps its id and bumps
`Vehicle.NetGeneration`, which tells a client that its state jumped.

## Snapshots and hashes

`CarSnapshot.Capture(car, combat, driver)` (or `SimSession.Capture(driver)`)
holds one car's whole simulation state as values:
- the body (pose, motion, controls, flags, timers, the contact record);
- its parameters (which damage lowers), engine and wheels;
- its arms;
- armour, chassis and every damage part;
- the armoury: selection, link group, and each weapon's ammo, timers, aim
  and hit points;
- the radar target;
- the driver's key ramps.

`Restore` puts the values back, and also the objects the car held. Plain
objects are copied field by field (`FieldCopy`), so a field added to
`Wheel`, `Engine`, `Weapon` or a damage part is covered without being
listed. The world's rand stream and the rounds in flight are not part of a
car. The network client's prediction restores its car from the server's
`OwnMotion` instead (below, "Prediction"): a wire format of what the step
carries between ticks, not this in-memory copy.

`SimSession.StateHash()` is a 64-bit FNV-1a over the session's whole state: its save snapshot's body
([savestate.md](savestate.md)) without the events the views have not taken, so it covers whatever the snapshot
does (the snapshot guard keeps that complete). `StateHash` can also hash a single car.

## The network melee

`MissionSetup.Build(..., melee: roster, meleeRules: new MeleeRules { Network = true, ... })`
sets up an arena with no player's car. `MeleeGame.AddPlayer(car, name, team)`
adds each human. The players are added in a fixed order before or after
`Start`. The rules:

- **Players.** Each human's car is built with the player's damage rules.
  It is placed at `PickSpawn` and listed in `World.Humans`.
  Its entity has the player bit and the team in flags bits 16–18. Team 0
  means free for all.
- **Score.** A kill scores trunc(1000 × victim mass / killer mass)
  (the original's rule), on the destroying hit only
  (`DamageModel.KilledBy`). It applies to AI and human victims alike. One's
  own death scores nothing. In team play, neither does a teammate's.
- **Respawn.** A destroyed player asks for a fresh car with RAISE_DEAD (Space; `CarCommands.RaiseDead`,
  `MeleeGame.RequestRespawn`), refused until `RespawnDelay` (5 s) after the death and once the game is decided; there is
  no automatic respawn (below, "The original's rules"). The car comes at a free spawn point, renewed in place: every
  wheel field, the controls and the contact record are reset, and `NetGeneration` goes up. A car reconfigured while
  dead (below) is rebuilt in place as the new car.
- **Regen pads.** Each player has their own regen timer: 400 points and a
  fresh car, at most once every 30 s, and only after leaving the pad.
- **Targets.** Without team play every car is its own side for the radar's
  T and E (`Combat.FreeForAll`): the players and the AI fill are all on team 0
  there, and the original's test (another team) alone left nobody to target.
  With team play the test is the original's: another team, team 0 (the AI
  fill) included in a network session.
- **AI fill.** The roster's cars attack the nearest live human every frame.
  A wreck is replaced by the same car at a free spawn point, at the same
  skill. The fill is on team 0.
- **Deaths.** One's own weapon or a teammate's books nothing; another side's car or round a death; no killer (a crash,
  SELF_DESTRUCT, the end's blow-up) a death and −500, floor 0 (network combat; Nitro: not in team play).
- **Leaving.** A leaver's live car is blown up and left as a wreck, taken out 10 s later, as an AI wreck is.
- **End.** `Ended` becomes true at `MaxScore` (a player's score, or a team's
  total in team play) or at `TimeLimit`.

### The original's rules

Read in the original game and the Nitro Pack and, for the respawn, confirmed in the real game
(a fake one-player session): the car never came back
by itself in 43 s; Space 3 s after a death was refused, 6.5 s after it brought a car at a new spawn point.

- **RAISE_DEAD** (game key 0x26, Space): a dead player's request, refused until 5 s after the death and once the game has a winner (the winner's id); the host builds the car at a spawn
  point. The wreck timer's own respawn request fires only on a car
  flagged 0x100000, which only the host's SR handler sets on a *remote* player's fresh car (cleared by its first `ST`):
  the player's own car never comes back by itself. I26: the input's `RaiseDead` command, the server's
  `MeleeGame.RequestRespawn`; the client's predicted car stays dead (it does not drive) until the respawn's snapshot.
  The AI fill keeps its own wreck respawn. Headless bots (`i26-server --bots`, the tests' and load tests' bots) press
  it 5.5 s after a death and each second after.
- **Deaths** (also in the Nitro Pack): booked only with no killer (a death and
  −500) or a killer of another team (a death); in a free for all every other machine's car is another team, the own car
  is not. So one's own mine or mortar splash books nothing, a teammate's kill books nothing for the victim, a kill by
  another a death. Nitro skips the −500 in team play, race and CTF.
- **The end** (the original's start-up): each machine says it in its own words, "*** You have
  reached the maximum score! You win!" / "*** %s has reached the maximum score! You lose, chump!" (the "Team %s" forms
  in team play; Nitro "*** %s has reached %d points/kills/captures! %s", "*** %s has completed %d laps! %s"); the
  winner's machine turns its camera to the spinning death orbit (camera 0xfefe, mode 8), a loser's blows its own car up
  (no killer, so −500 and a death); the mission ends 10 s later. The server sends `EndEvent` (winner's
  slot, team, count, or the time limit) and each client composes its line (`NetLines.End`); the base game's time limit
  and kills goal (I26's extensions) take Nitro's flow.
- **Leaving**: the leaver's car is blown up, not removed; nothing is booked (`Departed`).
- **Teams**: eight (Grey Hounds … Men In Black, `MeleeGame.TeamNames`), team = player data byte 1 − 1 in flag bits
  16–18. I26 keeps 0 as nobody's (the free for all, the AI fill), so its teams are 1–8 and the eighth is
  `Entity.Team8`. CHOOSE A TEAM picks all eight.
- **Game keys** (the original's game-key dispatch): group B (views, mirror, self-destruct, chat, names, the host's invite and
  boot keys) needs a living player and no winner yet (the audits called this "the session
  started": it is the winner's id).
  The port's RESET_UPRIGHT (id 0x70, U; `CarCommands.ResetUpright`, bit 27 of the commands the wire already carries, so no protocol change) is a fifth, on the Control Configuration's left column. The server applies a client's press
  (`UprightReset`: the terrain under the car, upright, heading kept, at rest; the car's `NetGeneration` bumps so every client snaps to it, and a prediction does not run it itself) unless the host turned it off
  (`ServerOptions.AllowCarReset`, the host form's Car Reset, `--no-car-reset`; default on; listed in `SessionInfo.CarReset`, the discovery answer's last byte, an older host's missing byte reading as off). It never works in a cut-scene (a co-op player's own camera scene too) or while dead.
- **Ignition stays on** (I26, #108; protocol 23): the host's rule that nobody switches their engine off in the game, so no car
  hides from turrets with its engine off. `ServerOptions.IgnitionStaysOn`, off by default: the base host form's Ignition Lock row
  (melee and co-op), IGNITION STAYS ON in the Nitro form's game options (melee and co-op) (shell.cfg `[port] ignition_stays_on`,
  written only when on), `--no-ignition-off` for a listen host and `i26-server`. A melee's rules carry it
  (`MeleeRules.IgnitionStaysOn`, saved in the savestate's build, so a migration's successor keeps it), a co-op host sets it on its
  session; the session's input handling (`SimSession.IgnitionStaysOn`) drops the ignition key while the engine runs, and still
  lets it crank an engine that is off. The join accept carries it (`JoinAccept.IgnitionStaysOn`) so the client's prediction
  drops the key the same way, and the game's listing shows "Ignition stays on" (`SessionInfo.IgnitionStaysOn`, with the host's rules after the makes and models).
  The Control Configuration lists four of them in the right column's empty rows, named as the menu names the rest (Chat (online),
  Self Destruct, Respawn (online), Show All Names) with a hint on the left's two (chat, and Ctrl+Del then Space for a new car).
  Left or right click presses a key, with Ctrl, Shift or Alt held. The install's gamekey.map is never written: the choices
  go to `user://gamekeys.cfg` (game key id → key text) and `GameKeyMap.Load(text, mine)` lays them over it (a game key's own keys
  go, the key leaves any other game key); Restore goes back to gamekey.map's. The other game keys (views, Pause, Ctrl+Q ...)
  stay gamekey.map's.
- **Weapon cycle back**: Mouse Options' Wheel Up / Down on Cycle Weapon sends the wheel's step back as `CarCommands.WeaponCyclePrev` (bit 28 of the
  commands the wire already carries, so no protocol change of its own), which the server's input check lets through and a client's prediction runs, as
  `WeaponCycle`.
- **Chase crosshair** (protocol 22): the port's aiming aid in the F2 chase view (Mouse Options' Chase Crosshair, `CrosshairPolicy`) shows in a
  network game, melee or co-op, only where the host allows it: the host form's CHASE CROSSHAIR (the base form's Chase Crosshair row, CHASE CROSSHAIR in the Nitro form's game options; shell.cfg `[port] host_crosshair`, written only when
  on; `--crosshair` / `--no-crosshair` for a listen host and `i26-server --crosshair`; `ServerOptions.AllowCrosshair`, off by default, also in
  the migration's options). `JoinAccept.Crosshair` (last byte) tells the clients, and each player's own setting then decides; the listing
  carries it (`SessionInfo.Crosshair`, the discovery answer's last byte, a missing byte reading as not allowed) and the session popup says
  `Chase crosshair allowed`. The server does nothing with it and nothing in the simulation reads it.
- **Turrets** (protocol 23): the turret slew of the 1998 build (GOG's and the Nitro Pack's, the default) or of the 1997 CD build
  (docs/deviations.md "Turrets (the build's slew)"). Offline it is Play Options' Turrets; in a network game, melee or co-op, it is the
  host's: the base host form's Turrets row (1998 (GOG/Nitro) or 1997 CD, a click flips it), TURRETS in the Nitro form's game options
  (shell.cfg `[port] host_turret_slew`, written only for 1997), `--turrets-1997` / `--turrets-1998` for a listen host and
  `i26-server --turrets-1997` (`ServerOptions.Turrets1997`, off by default, also in the migration's options). The server sets it on its
  combat (`Combat.TurretSlew1997`); `JoinAccept.Turrets1997` (the last byte) gives it to the clients' mirrors for their prediction, and the
  listing carries it with the host's rules after the makes and models (`SessionInfo.Turrets1997`); the session popup says "Turrets as on
  the 1997 CD". Online the health term is 1 in both builds, so only the rate differs (1.5 against 1.7 rad/s, 6.0 against 6.8 a helicopter's).
- **Console lines**, `NetLines`:
  "*** %s has joined the game" / "*** %s has left the game" (Nitro "*** Player %s ...", "*** A %s team member has joined
  the game!"), "*** %s has been killed" (no killer), "*** %1:s was killed by %2:s", chat "From %s: ", the entry "Say: ".
- **The score window** (toggled from the held actions show_player_scores
  `'` and show_team_scores `;`; I26: one view on Tab or a pad's Back, the teams' totals heading their players): up only while its key is held, the console's lines hidden meanwhile. "# Player Name
  Score Kills Deaths" (a race's "Laps", CTF's "Captures"), players in slot order, unsorted; the teams' window (team play
  only, and not while `'` is held) sums each team and sorts by score. On the host's machine its own row is marked H and
  the others numbered for BOOT_n. I26 draws it as a panel of columns with the ping, a page at a time (below). The
  ping (there and on the HUD) is the lowest of the transport's last ten round-trip samples (`PingWindow`): under packet
  loss its samples read far above the link's round trip.
- **Boot** (BOOT_1..7, Ctrl+1..7 → dpDestroyPlayer): "Boot %s from the game? (CTRL-Y /
  CTRL-N) " for the n-th numbered row; Ctrl+Y boots, Ctrl+N cancels. I26 (`NetHud.HostKey`, `NetMeleeServer.Kick`):
  the listen host's score window pages nine numbered rows (the own H row besides), Ctrl+1..7 as the original's and the
  port's Ctrl+8 and Ctrl+9 for the rest of the page, Ctrl+PgUp/PgDn turning it; a booted player is told "The host
  booted you from the game" (the original said nothing; its session just ended), the others see the leave line, and
  the car blows up as a leaver's. `i26-server`'s console: `kick NAME|SLOT`.
- **Bans** (I26, #117; `NetMeleeServer.Ban`, `BanList`): the listen host's chat line `/ban NAME` (a slot, a name or a
  unique start of one) boots the player and refuses their joins for the rest of the game ("The host has banned you
  from this game.", the refusal's text: no wire change); everyone sees "*** NAME has been banned by the host". `/ban`
  alone lists the bans, `/unban NAME` lifts one; `i26-server`'s console has `ban`, `unban` and `bans`. A ban holds what
  the server knows of the player: the name (case does not matter) and the address they joined from; a join matching
  either is refused, before the password. Limits: another name from another address (a new connection, a VPN) gets
  in; everyone behind the banned public address is refused with them; a loopback address (players on the server's
  machine) is never held; a name not in the game is banned by name only; bots are booted, not banned. The list lives
  in the server for this game: the dedicated server keeps it across rounds, a host migration's new host starts
  without one.
- **Invite only** (cfg bit 2; INVITE_ACCEPT / DECLINE / SKIP, Ctrl+Y / N /
  H): every join request queues; the host sees "Let %s join the game? (CTRL-Y / CTRL-N) " for the oldest; Ctrl+H puts it
  at the back keeping its time; one unanswered 30 s after it asked is turned away. The joiner is told "The host has
  rejected you. Go find another game, pink boy." or "Your request timed out. Try again." (the original front end's). I26: the
  server holds the connection (it counts toward the seats) and sends `JoinWait`; the loading screen says "Waiting for
  the host to let you in..."; a listen host's own first join goes straight in (`ServerOptions.ListenHost`); the anet
  listing shows an invite-only game as joinable. A dedicated server (`--invite-only`) asks on its console ("*** Let NAME
  join the game? (y / n / s; turned away in 30 s)"); unanswered, the join is turned away, as the original's.
- **Strict car checks** (the Nitro Pack's car check): besides the garage's rules
  (`CarConfigCheck`), `CarConfigCheck.Strict` refuses the same special twice, a second turret, a dropper off a dropper
  mount, a gun on one, a turret off a turret mount. The tank cannons and the howitzer were always refused online:
  `HiddenContent.Check` refuses the howitzer always and the cannons without the host's Hidden Weapons & Cars, which
  also lets a hidden car (the helicopters' two turrets, a secret body) keep its own mounts. Every stock melee car passes.

### Reconfigure (I26's)

Not in the original (docs/deviations.md). While dead in an undecided game, Enter opens the join form's car panel over the
game in a hosted shell (`ReconfigureScreen`: record 18's art, MAKE / MODEL, VARIANT, CONFIGURE CHASSIS with the player's
own saved variants, JOIN GAME to send, CANCEL or Esc to leave it). The car goes as `Reconfigure` (its file and, for the
player's own variant, its .vcf), and the server checks it as a joining car (`MeleeHost.Reconfigure`: the garage's
rules, the models and weapons offered, hidden content, the restrictions, the strict checks; a car the server lacks
without a configuration is refused rather than stood in for), answering `ReconfigureAnswer`: taken (the file it will
build), or refused with the reason, shown over the form, the old car kept. Taken, `MeleePlayer.NextCar` is set and the
next RAISE_DEAD rebuilds the car in place (`MeleeGame.Rebuild`: the same vehicle object and id, every field of a fresh
build, its damage bound to it again; `CarEpoch` counted). The server sends `CarChangedEvent` before the respawn's
`RenewedEvent`; a client rebuilds its copy at once on receipt (before the snapshots are read into it), keeping it a dead
wreck where it was until the respawn's snapshot; the own-car block carries the build (`OwnCar.Epoch`), and the
prediction passes over a snapshot of another build (one of the next waits for the event). The game rebuilds the car's
node, and for the own car its cockpit and cameras. The car is kept for the next round. At most one change a half second
is answered.

Single player (`MeleeRules.SinglePlayer`, the default) keeps the
original's paths:
- every opponent attacks the player;
- the waves;
- the kill credit through the script host;
- one regen timer;
- the end 5 s after the player's death.

### Car paint (protocol 17)

A player's respray (`CarPaint`: a primary and a secondary colour and a finish, kept in the variant's `.i26` sidecar, never in the .vcf) is
cosmetic and travels with the car choice: `JoinRequest.Paint` on the join, `Reconfigure.Paint` while dead (a new paint alone is a rebuild too,
announced by the car-changed event), and `CarInfo.Paint` to every client in the join accept, the spawn and car-changed events and the
CarList answers, where `ClientWorld` puts it on the mirror's `Vehicle.Paint`. The server checks only that the bytes parse (`CarPaint.FromBytes`;
else the factory paint). It sits on the seat's `MeleePlayer.Paint` and the vehicle, survives respawns (`MeleeGame.Rebuild` keeps it), is
saved in the snapshot (the VTAB entry and the melee players; savestate format 13) and so survives host migration, whose join records carry
it too. A bot seat takes the paint `ServerOptions.PaintOfCar` gives for its chosen car (the game supplies it from the saved variant's sidecar). A co-op
seat's car keeps its player's paint through the mission's respawns.

### Many players

A game takes up to `Protocol.MaxSeats` (64) players. Arenas have 8–17 spawn points, so the spawn rule has a case the original never
met, when every point has a live car within its radius + 20:

- The pick is the original's (a random point, then the next ones in turn) whenever a point is free: the same random draws, so
  single player and small games are unchanged.
- With every point busy, the point whose nearest live car is farthest is chosen. If even that point has no room for a car,
  `MeleeGame.Place` offsets the car from the point on rings along the point's right and forward axes (a car and a half apart, nearest
  first): on ground within 1.2 m of the point's height and no steeper than a ramp on the way (no cliff edges), not inside a solid
  placement (node boxes, type-4 spheres, mesh footprints: a spot under a bridge deck counts as inside), a car length from every live car.
  Six rings (about 100 spots); with none, the car stacks as the original's.
- Joins, respawns, regens, the AI fill's replacements, CTF's team tables and a race's grid all go through it, deterministically.

### Bots

Bots are players the server drives, an option of the host (not in the original: docs/deviations.md). `MeleeGame.AddBot`
(`MeleeBots.cs`; `ServerOptions.Bots` and `BotSkill`; the host form's Bots, `--bots-ai N [--bot-skill easy|normal|hard]`) seats
them as players. They are not the AI fill (`--ai`, which attacks the nearest human whatever the game is and is not scored), and not
the headless test clients (`--bots`, real connections for load tests): the three exist side by side.

**A bot is a seat.** It is a `MeleePlayer` with `BotSkill` above 0, listed in `MeleeGame.Players` like a human, so everything the
melee does for a player it does for it: the car is built by `Build(file, true)` (player damage, a dead car left for RAISE_DEAD),
the team is in the entity's flags, the kills, laps (a race's counter), captures, score and deaths are booked by the same rules, the
goal and the end count it, the regen pads give it its 400 points, and a destroyed one is brought back by RAISE_DEAD after the
5 s (`RequestRespawn`, asked a third of a second to a second and a half after the delay, as a finger would). It has no connection,
`Driver` or inputs: the keys are replaced by the AI. In the world its car is a human's (`World.Humans`: the player's step and damage rules)
but `Combat.IsBot` makes its weapons fire as an AI car's, with no missile lock held (nobody holds the selection a lock needs).
- **Slots.** A bot's roster slot is `Protocol.MaxSeats - 1` counting down (`MeleeMode.AddBot`), a human's the lowest free from 0 up,
  so they never meet while humans plus bots fit `MaxPlayers` (at most 64). `MeleePlayer.BotSlot` keeps it. `MeleeHost.Bots` (the mode's) are the
  `BotSeat`s (slot, name, player); `Everyone()` lists seats and bots by slot, and the scoreboard, the score and goal events, the kill lines, the
  end event and the standings, and the clients' car roster (`CarInfo.Owner` is the slot, so a client lists a bot as a player) all read both.
- **Names and cars.** A driver of the melee form's 53 A.I. names (`MeleeDrivers.Names`, the original front end's list, which the headless bots and the AI fill take theirs from too) not already at the table,
  with " (bot)" after it: the name is the mark on the scoreboard and in the feed, since the wire has no field for it (`BotNames.IsBot`). A car at
  random from `ServerOptions.BotCars`, stock cars first variants (Piranha, Courier, Eagle, Aztec, Stag, Marx, Pony, Royal, Amazon on the base game's
  list; the Nitro Pack's models are the same stems) the install has and the game's hidden-content rules let in. Under WEAPON RESTRICTIONS a bot (and the AI fill) drives its rolled car with the restricted weapons taken off, all of them under All Weapons (`MeleeHost.Legal`): never another model or a stand-in, and served by content as a player's own variant is (`CarInfo.Vcf`), so every machine, the host's own screen included, builds the same car, and a successor host gets the same file from the savestate's served cars and the options' restrictions. The original had no server-driven cars to restrict, so this is the port's reading of its rule: a game that allows no weapons has none on any car.
- **A.I. cars.** The host picks each bot seat's car (`ServerOptions.BotChoices`, one car file per seat, empty for Random): on the host form the A.I. Cars
  row (the Nitro form's GAME OPTIONS has an A.I. CARS line) opens the A.I. CARS popup (`BotCarsPopup`; mouse and pad), a row for each bot seat up to Bots, each with the
  instant melee's MAKE / MODEL and VARIANT (stock cars, the host's saved variants, hidden models when Hidden Wpns & Cars is on). The choices are kept in
  melee.cfg (`bot_cars`), not in the original's .mel files; i26-server takes `--bot-car N=MODEL[:VARIANT]`. Bot seat N is the Nth bot slot counting down from `MaxSeats - 1`
  (`MeleeMode.AddBot`), and whenever a bot enters that seat, at the start or when a human leaves, it drives choice N; so a bot sent away for a human and its seat's
  next bot are as the choices say. Seats past the list, or on Random, roll as before. The game's rules still apply (`MeleeHost.BotChoice`): WEAPON RESTRICTIONS
  take the restricted weapons off a chosen car (the same file as served for a player's own variant), a hidden model is let in only with the host's Hidden Wpns & Cars, a car the
  install lacks (a saved variant the successor does not have) rolls. A saved variant (a model's file the install's archive lacks: the host's garage saves, addon\) is served
  to the clients by content as a player's own variant is. **Random under restrictions:** a bot on Random prefers a random one of the host's saved variants that carry none of
  the restricted weapons as they are (`MeleeHost.LegalSavedCars`; with All Weapons restricted, the weaponless ones), else the rolled stock car stripped as above. The choices are in
  the savestate's options (the migration extras), so a successor host fills a seat the same way; no protocol or savestate change.
- **Teams.** None in a free for all. In CTF the team with the fewest players. In other team play the smaller of the teams the humans are on
  (two, if they are all on one: the next one over), so that bots fill the side that is short.
- **Skill.** The host form's Easy, Normal and Hard are the AI's setSkill tiers 4, 3 and 1 (`MeleeGame.SkillTier`; the single-player melee's
  waves go 3 to 1): `AiBlock.SetSkill` for its steering, throttle and aim, and the tactic table's aggression row `tier - 1`.
- **Seats.** Bots take the seats humans leave free: `MeleeMode.BalanceBots` keeps `min(Bots, MaxPlayers - humans)`. A human who joins a
  full game sends the last bot away (its car blown up as a leaver's, a `LeftEvent` to the clients); one who leaves lets a bot come back (a
  `JoinedEvent`, a new driver at zero). Only humans count toward the full check (`NetMeleeServer.OnRequest`), the listing counts humans and lists
  everyone, the game's rounds start with the bots again.

**What they do** (each frame, `BotsFrame`; the AI's own tick does the driving and the shooting, `Host.Order` as a mission script gives it):
- **Combat.** `AiOrder.Attack` on the nearest enemy (another side's player in team play, any other player in a free for all; the AI fill
  always), kept until it is dead or a car 40% nearer shows (the tactic is chosen again on a new target). With none, a goto across the arena. A hurt bot (under 40%) that
  is 30 s past its last regen goes to the nearest pad, shooting what is in its way. The tactic table, the avoidance, the escapes
  are the original's.
- **Race.** `AiOrder.Race` along the checkpoints from the one the car's `RaceTracker` wants next, two laps' worth in the order list (the AI loops
  it), through the planner (`PlanPath`: the road route where the car and the point are on one road, else the A*). The tracker counts in a 30 m sphere and
  a racing bot's AI moves on at 25 m across the ground (`Entity.NavReach`; the AI's own 40 would turn it for the next checkpoint before it was in
  this one); a bot that is still a point ahead of the tracker for 2.5 s (a checkpoint well above or below the car) is sent back for the one it missed. It fires at whatever is ahead and near, as
  a human may (weapons are as free in a race as in combat). A new order starts at its first point (the AI's racer scan, would otherwise pick the nearest).
  A bot that comes back at a spawn point (behind check1 on every stock arena) wants the checkpoint it wanted, as a human does (the count goes on
  through a respawn, as in the original), and drives the course to it (`MeleeGame.LeadIn`): from the leg of the course nearest the car, the
  checkpoints in order (passed, not counted) up to that one; on the leg out of it (the last checkpoint, seen from the start) it goes straight back.
  Straight at it from the start, the planner took it across the arena into the walls and drops the course goes round, and it died and came back
  to try the same again. Its 90 s without a checkpoint start again when it comes back, and a point of the course passed on the way counts.
- **Capture the flag.** Roles by seat order in the team's bots: raider, defender, raider, escort, defender, and again. A carrier goes for the stand
  of its colour. A raider goes to the nearest enemy flag at its stand (`CtfGame` has no dropped flag: a carried flag goes home when its carrier is
  removed or comes back, so there is none to return) and takes it by touch. A defender meets whoever comes within 110 m of its stand, else circles it. An
  escort follows its team's carrier (`AiOrder.Follow`), else attacks. Whoever is not a raider on the way hunts the carrier of its team's flag (when it is alive:
  a dead one's flag waits for its respawn).

**What is the port's own** (each is in docs/deviations.md as one line):
- *Road routes are not taken the long way round* (`Entity.DetourLimit`, a bot's 2.5 times the straight line plus 100 m; `RoadRoute.TryRoute` and
  `ExpandRoadPairs`'s `detour`). Nitro's race roads are open at the start line, and a goal across the seam is the whole road away; the A* takes it. The original's AI has no limit.
- *A racer drives into a checkpoint before it turns for the next* (`Entity.NavReach`, 25 m across the ground in a race, the AI's 40 m otherwise):
  with the AI's 40 m, a checkpoint less than about 50 m from the one before it and off the line was left before its 30 m sphere took the car, and
  the bot went back and forth between the two.
- *The collision test is off in a race* (`Entity.Avoid`, the speculative test of `Candidate`, which slows a fast car at every bump) until the
  car has hardly moved for 4 s, when it is on for 10 s to back out (`StuckCheck`).
- *A goto that is not getting nearer is given the grid's route* (`Ai.GridRoute`; `MeleeGame.Go`): the AI's A* has a pool of 1000 nodes, so a goal
  behind a long wall, as the two bases of a 2-team CTF arena often are, is never found and the bot circles; 25 s without getting 30 m nearer, and the bot is given
  the way found on a grid of 12 m cells over the terrain's impassable cells, as points 70 m apart, which the planner then plans leg by leg (90 s of it, then the planner's own again).
- *A bot that is stuck gives itself up*: 40 s barely moving, or in a race 90 s without a checkpoint, and it blows itself up (SELF_DESTRUCT: a death, no
  kill) and comes back at a spawn point; the next such life drives with the test on if the last was without.
- *Team play holds fire*: `ScriptHost.HoldFire` (`MeleeGame.FriendInLine`): no shot while a teammate is nearer than the target on the line (11 m) or going to be in half a
  second, or beyond it within 8 m and 150 m, or within 12 m of the target. `HoldWeapon`: no bot lays mines or blox (they would be driven over by its own side and itself).
  Over 15 game minutes of six Hard bots on m02 two teams: about 2100 hits on the other side, one on a teammate.

**Over the network** the protocol is unchanged (13): a bot is a car whose `CarInfo.Owner` is a slot, a row of the scoreboard with an alive flag and
a zero ping, a `JoinedEvent` and `LeftEvent`, `ScoreEvent`s, `KillEvent`s and, for a winner, an `EndEvent` with its slot. No client needs to know what a bot is;
the game's HUD and score window show its name with " (bot)". The cars the clients draw are the server's, as any player's.

**Host migration.** Bots go with the game: a savestate carries each bot's `BotSkill`, `BotSlot`, `Brain` (its target, the path it was
ordered along, its race route and timers: `BotBrain.Save`) and its entity's `OwnPath` (a path a savestate names by its entity: `SaveWriter.Path`'s kind 3). The successor's
`RestoreMigrated` rebuilds the `BotSeat`s from the players under their slots and the replay of the host's inputs plays the bots too, so the successor's game is the host's
(`ASuccessorHostTakesTheBotsOverAndPlaysTheSameGame`: the state hash after 20 s of replay is the host's). The successor's options carry `Bots` and `BotSkill`
(the savestate has them), so the bots stay while seats are free.

**Weaknesses.** The AI was written for combat on open ground and mission roads, not for racing, and the bots show it. Four Hard bots racing three laps
(12 game minutes at most, the tests' way): N23, N34, N21, N31, N26 and N35 finish in 2 to 5 minutes (best laps 25, 51, 66, 73, 35 and 90 s; a person's would be shorter, but nobody has measured one), N28, N22, N29 and N30 in 5 to 11 (laps of 92 to 217 s), N27 is a 5.8 km out-and-back whose 223 s laps do not fit three in twelve, and N24 and N25 do not finish (a pinch and a hazard
the A* does not see, the stuck bot blowing itself up again and again: 35 to 40 deaths in the run). Deaths are at walls, drops and water (3 to 40 in a run on four bots). A barricade a track means a player to shoot (N28's) is a wall to the A*: a
bot goes round. In CTF the bots capture on the three- and four-team arenas (N12 116 s, N14 116 s, N17 111 s for the first, ten or more in ten minutes) and on the two-team arenas mostly not (N11, N13, N20, N41, N43, N44, N46; N40
sometimes): the long way round a wall the A* and the grid do not manage, or a jam at a narrow place. In combat a fight is a long one: the bots spend a good part of their time in the AI's avoid and back-away states,
regenerate when hurt, and kill about one car a minute with eight on m05 (one in six with six on m02).

## The Nitro Pack's race and capture the flag

### The Nitro host form

On a Nitro install MULTI MELEE is the same path (HOST/JOIN, IPX/INTERNET; bank 56 sprites 4, 8, 12, 16) to the Nitro
form. GAME TYPE picks `MeleeKind` and the CTF team count and lists only that type's arenas (`.cbt`, `.rac`, `.cf2`..`.cf4`,
named by their WRLD). GAME OPTIONS steps team play (forced on in CTF), the limit (combat: points, kills, minutes, none;
race: laps, minutes, no limit; CTF: captures, minutes, no limit), its value (defaults 20000 points, 20 kills, 3 laps, 3 captures, 10 minutes)
and invite only. They go out in `NetLaunch` (`Kind`, `Goal`, `GoalValue`, `Minutes`, `CtfTeams`). `SessionInfo` carries
the kind, goal, value and team count after the drivers (an older reader ignores them); the roster tags a race "Race"
and a CTF game "CTF3". `tools/lan-test.sh` takes `I76_LAN_NAV`, `I76_LAN_HOST_IN`, `I76_LAN_JOIN_IN` and `I76_LAN_NET_TEST`
for the Nitro menu's clicks.

### WEAPON RESTRICTIONS and hidden content

The form's WEAPON RESTRICTIONS (the original's hotspot 5, 31..193 × 310..326) opens the original's checklist (bank 30
sprite 43 at 34, 25; rows and grid read from the install's the Nitro front end, `RestrictionTable`) with the port's rows for
the hidden weapons (`HiddenContent.ChecklistRows`); the clicks and the list it makes are `RestrictionChecklist`
(docs/hidden-content.md §4). The list is the original's: everything, or GDF (class, tier) pairs with tier −1 for a whole
class (`WeaponRestrictions`). It is kept for the shell's run, not saved, as the original's table.

- **Out.** `NetLaunch.Restrictions` and `NetLaunch.Hidden` (the host's Hidden Weapons & Cars, from any host form) become
  `ServerOptions.Restrictions` / `AllowHidden`; `--host-listen --restrict SPEC --hidden` and `i26-server --restrict SPEC
  --hidden` do the same. SPEC is `all`, a group (`slugs`, `mortars`, `missiles`, `flames`, `droppers`), a `.gdf` stem
  (`gchades`) or `class:tier`, comma-separated.
- **The rule** (`MeleeHost.CheckJoin`, before the connection is accepted): after a player's own variant passes the garage
  check, the car the seat would get (that variant, the install's car asked for, or the stand-in for one the server lacks)
  must be a model the melee list offers (`CarTable.OfferedStems`: the base table or nitcar.def), or with `AllowHidden` a
  complete hidden one; its weapons ones the pickers offer, or with `AllowHidden` complete hidden ones; and none the
  restrictions match, by the server's own `.gdf` class and tier. A refusal names the weapons ("Restricted weapons: HADES
  Cannon"); the client shows it on the form it came from.
- **Wire** (protocol 12, appended to `SessionInfo` after the locked flag): a flags byte (1 hidden content allowed, 2 every
  weapon restricted), a pair count (at most 64) and the pairs (class `i8`, tier `i16`). An older answer ends before them
  and reads as no restrictions.
- **Unlim. Turrets.** The host forms' UNLIM. TURRETS (the Nitro form's GAME OPTIONS, the I76 form's Unlimited Turrets row; saved in the host settings, off by default; `i26-server --turrets`) lets the game's cars carry several turrets, each on a roof mount (docs/deviations.md). Off, `CarConfigCheck` refuses a configuration with a second turret (DONE's notice 26) and `Strict` a joining car's; a bot's car keeps its first turret and loses the rest (`MeleeHost.Legal`). The listing's direct answer carries it as flag 4 beside Hidden and All Weapons (`SessionInfo.Turrets`), the session popup says "Unlimited turrets", and a joiner's own check names it before connecting.
- **The host's switch.** Hidden Weapons & Cars is Play Options' setting, and also a row on the host forms: the I76 form's Hidden Weapons & Cars row (hotspot 28), the Nitro form's GAME OPTIONS popup. It flips the same setting; the form's MAKE / MODEL list gains or loses the hidden models at once. Joiners bring hidden models and weapons when their own setting shows them and the game's answer says allowed.
- **Armour rule** (`CarConfigCheck`). The garage's DEFENSE buttons move 10 units between a zone, the chassis and the unassigned pool; the total never grows. So a variant passes when no value is negative and armour + chassis + pool is at most that of the model's heaviest stock variant (it used to compare each zone with the stock maximum, refusing what the garage makes by moving everything into one zone). With the host's hidden content on, the Rattler's secret bodies (`HiddenContent.SecretBodies`) are accepted with their own armour (stock + the secret's, no tyre or garage checks, their tracks are no garage part); off, the refusal says the body is a hidden car. Specials are 0..9 (0..11 on Nitro).
- **Joiners.** The join form's game popup prints the restrictions (by the checklist's names) and "Hidden weapons & cars
  allowed" under the drivers; JOIN checks the player's car against the game's answer first and says what is not allowed,
  without connecting. The server checks anyway.

Race (`.rac`) and CTF (`.cf2`/`.cf3`/`.cf4`) exist only in network games in
the original; single player stays the base melee. They are here server-side
(`src/I76.Sim/Missions/NitroModes.cs`, `MeleeGame`, `MissionSetup.LoadMeleePoints`).

- **Rules.** `MeleeRules.Kind` (`Combat`, `Race`, `Ctf`), `CtfTeams` (2-4),
  `Goal` (`Laps`, `Captures`, `Points`, `Kills`) and `GoalValue`. With no goal
  the kind's own applies (laps, captures) or points; a zero value falls back to
  `MaxScore` (only for a points goal). A zero value is unlimited: laps and captures
  then end the game only by the time limit, if there is one (the original's limit
  type has a "No Limits!" case too; `i26-server --goal-value unlimited` or `0`, and
  the forms' "No limit"). A rotation that changes the kind gives the new kind's
  limit (3) if the host set one, else none. CTF forces team play. A Nitro arena's extension names its kind
  (`ServerOptions.KindOf`).
- **Arena points.** A leading `#` is stripped. `spawn*` are spawn points (in CTF
  `spawnN` goes in team N's table, a team spawns only there), `regen` the pads,
  `checkN` the checkpoints (`check99` the finish), `a1flagN` the flags. Without
  CTF the flags are dropped.
- **Race** (`RaceTracker`). Per car: the next checkpoint in order inside a 30 m
  3-D sphere. The first `check1` pass only arms; a lap counts on each wrap
  (through `check99` if there is one). A lap adds 1 to the player's counter,
  which is `Kills` on the wire and in `MeleePlayer.Laps`/`Captures` (the
  original's one `+0x3c` field). Kills score nothing in race and CTF; deaths
  count. A destroyed car's checkpoints are not counted until it respawns.
- **CTF** (`CtfGame`). Flag states 0 inactive (the team has no player), 1 home,
  2 dropped (modelled, never entered, as in the original), 4 carried. An enemy
  picks the flag up from its home stand only; touching any stand of one's own
  colour with an enemy flag captures it (the flag goes home at once, and the
  own flag need not be home); touching one's own flag never returns it. The
  flag stays on the wreck and goes home when the car respawns, regens or leaves.
  A capture is +1 to every member of the capturing team and +1 death to the
  members of the flag's team. Flags are indestructible (they already were).
  **The flag's team** is the open question of the spec; it is taken from the
  number in the name (`a1flagN` is team N), isolated in `CtfGame.FlagTeam`.
  The stand's radius is a constant 4.2 m (the flag's bounding radius is not read).
- **End.** The first to reach the goal (a team's sum for laps, points and kills
  in team play, the team's captures in CTF) wins, the losers' cars are blown up
  (`DamageModel.Destroy`) and the game is over 10 s later. A time limit says "*** Time limit has elapsed!", every
  machine is a winner, nobody is blown up, and the game is over 10 s later (the Nitro Pack's per-frame game rules). In network combat
  a death with no killer costs 500 points (floor 0), the losers' end blow-up included. From the tick after the end
  every machine's own car is on the brakes with the wheel straight (throttle −1, steer 0, the keys' ramps not run:
  the original's input routine writes these and returns early while a winner is recorded): the server applies it to every human's
  car (`SimSession.EndBrake`), and a client's prediction from the end event's server tick (`MeleeClient.EndTick`), so
  the predicted car does not snap back. The original holds its car the same way while a network regen's fresh car is
  pending; here a regen's car comes at once.
- **Wire.** Protocol version 24 changes nothing in a message: it keeps builds apart whose ground past a map's edge differs (0.12.0 carried the map's ground on there; from 0.12.1 it is the original's plain at 0 near the map), on which a client predicts its own car. Protocol version 23 adds the host's ignition lock (below, "Ignition stays on"): `JoinAccept.IgnitionStaysOn` (a bool after `Crosshair`), and the host's Turrets (below, "Turrets"): `JoinAccept.Turrets1997` (the last byte); it ends the game's answer with the host's rules after the drivers' makes and models (an older host's answer lacks them and reads as the original's: Co-op Damage x1, Co-op Scaling, Rolling Start and the ignition lock off, the 1998 build's turrets), and it keeps builds apart whose host migration hands over a savestate of format 15, which has no grounded teleport paths (format 16, docs/savestate.md), and the other way round. Protocol version 21 changes nothing in a message: it keeps builds apart whose host migration hands over a savestate of format 14, which has no co-op gate hold (format 15, above, "Gates shut behind the player"), and the other way round. Protocol version 20 adds the co-op lobby: `MsgType.LobbyState` (server → client; the mission, the seats, the receiver's id and the host's, the players' ids, names, cars and ready marks, and the mission's own car), `LobbySet` (client → server: the ready mark and a car's file, .vcf and paint), `LobbyStart` (the host's start, forced or not), `LobbyChat`; the session list's answer ends with a byte for a game in its lobby and the anet tag's kind-3 goal nibble has bit 3 for it. The join accept is unchanged; it comes when the host starts the mission. Protocol version 19 adds the co-op camera scenes and movie pause: the snapshot tail's script byte has bit 4, the seats' personal cameras (a count, then a seat byte and a `CameraState` each); `MsgType.MovieDone` (client → server, reliable: the movie's name); `EventKind.MovieEnd`; and `CoopInfo` ends with the movie being played (a string, empty for none). The savestate is format 14 (a personal camera per seat and the scene a thread is in). Protocol version 18 raises a co-op game's seats from 4 to 8: the join accept's `CoopInfo` names up to 8 seats' cars (a version 17 reader refuses more than 4), and nothing else on the wire changes (the savestate's co-op build and the formation are counts, not masks: format 13 stays). Protocol version 17 adds the car's paint (below, "Car paint"): `JoinRequest.Paint` (after the password and the rejoin byte, which is now always written: 0 when the request is no rejoin), `Reconfigure.Paint` and `CarInfo.Paint` (after `Vcf`), each a u16-length blob of `CarPaint.ToBytes` (empty: the factory paint); a request or reconfigure without it still ends cleanly and reads as no paint. The host-migration record is format 2 (the seats' and invites' join records carry the paint) and the savestate is format 13. Protocol version 16 changes nothing in a message: it keeps builds apart whose host migration hands over a savestate of format 11, which a bot game's snapshot can fail to write (a bot given a new route while the AI's nav path walks the old one: format 12 writes such a path by its points), and the other way round. Protocol version 15 adds co-op story missions (above, "Co-op over the network"): their events, the join
  accept's co-op part, a byte at the end of every snapshot's tail (a melee's 0) and of the session list's answer (whether
  the game is co-op, then the game's simulation rate), and the anet tag's kind 3 (a co-op game; an older build passes it over). Protocol version 14 (0.7.0) changes nothing on the wire: it keeps 0.7 out of games with 0.5/0.6 builds, whose host migration hands over a savestate of format 9, which 0.7 (format 10: the bots) cannot read, and the other way round. Protocol version 9 adds the car's file to the cars the join accept and the spawn event carry when it is a player's own variant (`CarInfo.Vcf`), and the join request's configuration is that file. Protocol version 8 adds the arena's fingerprint to the join accept (the mission file's and the terrain's sizes and CRCs: a client without the host's map, or with another copy, refuses before building). Protocol version 6 (6 sends a structure's whole destroyed state, root and nodes, with the explosions that went with it, in the structure event and the join accept; 5 packs the jammer's "on" into bit 7 of each special's charges byte in the own-car block, so the cockpit plate lights for a client; version 3 had a ten-byte `CarInput`, its commands a u16: 4 widens them to a u32 for the hotkey commands). The join accept carries kind, goal, goal value
  and the team count; every snapshot ends with the flags (team, state, position
  or the carrier's position, the carrier's car id); the console's `***` lines
  (flag taken, captured, returned, a team left, the goal reached) are
  server chat events (`From = -1`); the game over's standings are in the goal's
  own count (laps, captures, kills, or points).
- **Host options.** `i26-server --kind combat|race|ctf --goal laps|captures|points|kills
  --goal-value N --ctf-teams 2-4` (the kind and teams default from the arena's
  extension), and the same four on the game's `--host-listen`. The shell's host
  form still has to set them: `Options.MeleeKind/MeleeKindGiven/MeleeGoal/GoalValue/CtfTeams`
  in `InstallLocator`, set in `ApplyNetLaunch` (`MainNet.cs`) from the launch.
- **HUD** (`NetHud`, in both looks). The goal line, top centre: "N of M Laps
  Completed", "Flag Captures", "Kills" or "Points" with the leader's count, or
  the time left ("00H:00M:00S Minutes Remaining") when there is a time limit.
  The scoreboard has the Laps or Captures column. CTF flags show on the radar
  in team colours (`Cockpit.Blips`) and as a pole and pennant (`FlagView`, drawn on the carrier's drawn car node while carried, so it moves with the interpolated or predicted car instead of stepping with the snapshots, and between `ClientWorld.PreviousFlags` and `Flags` when loose; a
  placeholder for the arena's own flag model, shown while the flag is away).
- **Tests.** `NitroModesTests` (lap logic, flag state machine, goal counts)
  and `NitroModeTests` (a race on `N35.RAC` and a CTF on `N40.CF2` through the
  host, skipped without the Nitro install).

## CB voice (protocol 20)

Push-to-talk voice chat in network games (melee and co-op), heard as over a CB radio: the game's own radio (Groove's lines on the Voice bus, the
handset in the corner of the screen) turned to the players' use. Hold the **Poem** key (the Controls action `POETRY`, C by default, rebindable in Control Configuration's Poem row; poetry
is single-player only, so in a network game the key is free and the client never sends it; there is no separate CB key, and an old `CB_TRANSMIT` line in gamekey.map is ignored; a pad's Misc1 button, the Share / Mic button, on a pad that has one) and everyone who
may hear you does.

- **Who hears.** The server relays a seat's frames to every other seat in a free-for-all and in co-op, and to the sender's team in team play
  (`VoiceRouting.Hears`: CB is one shared band, a team game keeps its plans in the team). Never back to the sender; bots are not seats and never
  talk. The relay does not decode (`NetMeleeServer.Voice.cs`); the dedicated server needs nothing but the rule. A host migration carries nothing:
  the relay is in whichever server runs, and a talk is only frames in flight.
- **The wire.** `MsgType.Voice` (client → server): u16 sequence, u8 flags (1 = first frame of a talk, 2 = last), the Opus packet to the end of the
  datagram; the server answers every listener with `MsgType.VoiceFrom`: the sender's slot first (the server's own, whatever the client wrote). Also `MsgType.VoiceMuted` (server → client, reliable) and `MsgType.VoiceIgnore` (client → server, reliable), see "Muting".
  Unreliable, 20 ms to a packet (50 a second, about 12 kbps of Opus plus 40 bytes of headers a packet: roughly 28 kbps up and the same down per
  listener while someone talks), at most `VoiceWire.MaxPayload` (120) bytes of Opus.
- **Limits** (`VoiceLimiter`, server time): 60 frames a second sustained with a burst of 12, a talk cut at 30 s without a one second pause (a stuck key
  or a client that never stops), an oversize frame refused; the excess is dropped and logged once per burst. The client stops at 28 s and says so.
- **Codec.** Opus through Concentus 2.2.2 (a C# port of libopus, BSD-3-Clause, the notices are in `THIRD-PARTY-NOTICES.txt`): 8 kHz mono, VOIP,
  12 kbps constrained VBR, in-band FEC with 10% expected loss (a packet lost is recovered from the one after it), PLC when that is lost too.
- **Receive.** A `JitterBuffer` for each talker: a talk starts playing when four frames (80 ms) are held, frames come out in order whatever the
  order they came in, a late one is dropped, a gap is conceded to the FEC or the PLC, a buffer more than twelve frames deep skips ahead, and a talk
  whose end packet was lost ends 0.4 s after the last frame. The pull is paced by the sound card's own buffer (a generator stream on the
  **Voice** bus, so Audio Control's Voice Level is the volume).
- **The radio** (`RadioVoice`): a 300 Hz to 3 kHz band (two sections each side), a compressor (3:1 above −18 dBFS), a soft clip (the carbon
  microphone's grit), a steady hiss at about −36 dBFS under the voice, and the band again over it all; a squelch burst of static when a
  talk opens and a click with a fading tail when it closes. The game has no CB click or static of its own (its CB lines are plain speech), so the
  squelch is synthesised. The sender hears the same click at its own key, not its own voice.
- **Capture.** `AudioStreamMicrophone` on a muted `CbMic` bus with an `AudioEffectCapture`. `CB Microphone` (Audio Control, under the three levels, in the
  front end and live in a network game; a click steps none, the system's default, each input device; shell.cfg `[port] cb_mic`) starts on **None**:
  a file with no choice is None, a saved device stays. With None the game never opens the audio input: `audio/driver/enable_input` is off in the
  project (macOS asks for microphone access when it is on at start-up) and is switched on at run time only when a device is first opened; the key does
  nothing but say once a session to choose a microphone, and receiving other players' voice is unaffected. With a device chosen nothing is opened
  until the key is first pressed, the device is closed again after 30 s of silence, and switching back to None closes it at once. Without any input
  device the first press says so in the console. The sound card's rate is brought to 8 kHz by `VoiceResampler`. The macOS export asks for the
  microphone with a usage description and the hardened runtime's audio-input entitlement.
- **Settings and commands.** Play Options: `CB Voice` (shell.cfg `[port] cb_voice`, on by default; off neither sends nor plays); Audio Control has `CB Microphone` (the incoming voice's level is the Voice Level there). In the chat line: `/mute NAME`, `/unmute NAME` (a name, or a unique start or part of one; kept for the session, not by slot),
  `/cb on` and `/cb off` (for this game), `/mute` alone lists who is muted.
- **Muting.** Three levels. (1) *For yourself*: `/mute NAME` / `/unmute NAME` (and `/cb off`, which mutes everybody). The client plays nothing of that
  player and also tells the server (`MsgType.VoiceIgnore`, reliable: a flag for everyone, else the slots; resent when the set or the roster
  changes) so the server stops relaying their frames to it: nothing is sent, so nothing is paid for. (2) *For everyone*, by the host:
  `/cbmute NAME` / `/cbunmute NAME` in the chat line (`/cbmute` alone lists who is muted) on the machine that runs the game (the listen host, and a
  successor after a host migration); anybody else gets "only the host can mute a player for everyone". The dedicated server's console has
  `cbmute NAME` / `cbunmute NAME` (no host seat there; booting works the same way: the lobby's and the score window's BOOT, `kick`, are the host's
  and the console's). `NetMeleeServer.CbMute` matches the name as the player's commands do (exact, else a unique start, else a unique part,
  case-blind; a player who has left can still be unmuted by name), keeps it in `MeleeHost.CbMuted` **by name** (so leaving and coming back does
  not undo it), stops relaying that player's frames (`RelayVoice`), says `CB: NAME muted for everyone by the host` as the server's chat line to
  everybody, the muted player included, and sends the player `MsgType.VoiceMuted` (reliable, one byte, also at the seat) so the client stops
  sending, and the HUD says `CB: you are muted by the host` (the modern look's `MUTED BY HOST` panel). A muted player never appears in the others'
  on-air list. The set rides in the host-migration extras (after the shot counts), so a successor keeps it. (3) *The game's CB radio off*:
  the host form's CB RADIO (the base form's CB Radio row, CB RADIO in the Nitro form's game options; shell.cfg
  `[port] cb_radio`, written only when off; `--no-cb` for a listen host and `i26-server --no-cb`; `ServerOptions.AllowCb`, on by default,
  also in the migration's options): the server relays no voice, `JoinAccept.Cb` (last byte) tells the clients, which neither send nor open
  the microphone, show no CB hint and say `CB is off in this game` on a key press; the listing carries it (`SessionInfo.Cb`, the discovery
  answer's last byte, a missing byte reading as on) and the session popup says `CB radio off`.
- **The screen.** While the key is held the CB handset (the HUD's zcbh art) is raised, in the modern look a `CB TRANSMITTING` panel with a blinking
  lamp sits under the chat, with a `CB name` panel for each player on the air in the team's colour; the 1997 look puts `CB: you are
  transmitting` and `CB: name` lines under the ping. Until the key has been used a quiet line reminds of it.
- **Tests.** `VoiceTests`: the Opus round trip of a synthetic signal (rate and level), the packets on the wire (the slot not trusted), the
  routing rule, the limiter, the jitter buffer (order, prebuffer, late, gaps, wrap, a lost end, a stall), a 10% loss with reordering, the
  radio's frequency response on a sweep, the squelch, the resampler, and the relay in a real server with three clients (free-for-all and team
  play: not echoed, the team filter, the flood limit), the host's mute (relay stops and restarts, everyone is told, the name matching, held
  across a rejoin), the ignore list, the game with the CB off and the migration extras. `tools/cb-test.sh` is the end-to-end run: two games, a listen host feeding a WAV
  in as its microphone (`I76_CB_INPUT`) and the client writing what it plays (`I76_CB_RECORD`; docs/dev-hooks.md).

## Co-op story missions

The original has no co-op: its story missions are one player's. Co-op (phase 1: single missions of both games, 1–8
players, each in a fresh car) runs them unchanged with a **human group** where the script has its one player. This is
the simulation's part (`src/I76.Sim`); the server's game mode, the script's presentation over the wire and the shell
come on top of it.

### Building one

```csharp
var setup = MissionSetup.Build(vfs, world, asm, requested, rand: rand, seed: seed,
    coop: new CoopSetup(["vppirna1.vcf", null, "vcgcour1.vcf"]) { RespawnDelay = 5f });   // or CoopSetup.Of(n)
var session = new SimSession(setup, SessionOptions.Network, rate) { Build = build with { Coop = coop } };
foreach (var car in setup.Humans) session.AddDriver(car);      // seat k drives with inputs[k]
var group = setup.Script!.Host.Coop!;                          // CoopGroup: Seats, LeadSeat, Happened
```

- **Seats.** Seat 0's car is the player placement's (`World.Player`), the requested car (or `Cars[0]`). Seats 1–3 are
  built as the player's (the shell's car, its body's sounds, the driver's handguns as a network melee's car has them)
  and stand in formation behind it (`SpawnSpace.Beside`: slots in its frame a car's clearance apart, straight behind
  first, then behind to either side, beside, further out and back; on standable ground, outside the structures, clear
  of every car, and **out of the other cars' way**: 8 m off the line each AI car within 400 m is heading along for
  200 m, and 6 m off each leg of the script's paths, the nav points its cars and cut-scenes drive to; with no slot out
  of them all, the one least in them; else the melee spawn spread's rings). The same placement serves a form-up, a
  respawn and a put-back, from where the cars are then. Each is in
  `World.Humans`, so the player's step and damage rules are theirs; their entities (`coop1`..) have the player's
  object flags. `World.Coop` is on: the rules the original gives its one player are each human's (below).
- **The lead** (`ScriptHost.Lead`, `CoopGroup.LeadSeat`) is the first living human: seat 0 (the host) while alive.
- **Events** (`CoopGroup.Happened`): `CoopRespawned(seat, why, beside)` (a death's or a join's fresh car, a leaver of
  the map put back), `CoopLeaveWarning(seat, sound, priority)` (the leave-map routine's CB line for that player's radio
  alone: it is not queued on the shared CB).
- **Players coming and going**: `ScriptHost.CoopLeave(seat)` takes a seat out of the group (its live car blown up, as
  a network melee's leaver's; seat 0's entity stays: it is the script's player actor), `CoopJoin(seat)` gives a free
  seat a fresh car beside a living teammate. The seats are fixed at the build (the snapshot's `SessionBuild.Coop`).
- Off (`coop: null`), and with it everything below, a mission is the single player's, bit for bit.

### What "the player" means

The script's player actor (every actor slot holding the player placement's entity: T16 and T17 have two `user`
actors; a variable holding such an index is the same slot) is the group, by use (`FsmVm`):

| Script use | In co-op |
|---|---|
| Conditions (`isWithin*`, `isWithinEnemy`, `isArrived`, `isAtFollow`, `isAirborne`, `isShot/Rammed/Attacked`, `hpLesser`, `canSee`) | true if any living human satisfies it. The thread remembers which (`Thread.Match`) until it yields: a following action on the player in the same tick goes to that human (T12's jump zone destroys the one in it, S03's jump camera watches the jumper). The next condition tries the last matched human first (`Thread.Prefer`) |
| Negations | fall out as "no human": a leash or escort fails once everyone is away, "no enemy near the player" holds only if none is near anyone |
| `isDead(player)` | every human dead at once (a respawning human is dead). The death-taunt routine's `stopCB` follows it, so it comes only then |
| `isEqualId(x, player)` | x is any human (the match). `whoX(victim)` names a human by a slot of its own (`NoLabel..`), never the group's, so `attack(friend, v)` goes at that human |
| `whoRammed/Shot/Attacked(player)` | the match's attacker, else the attacker of the human hit last |
| `isGroovesFault(x)` | any human's kill (`Entity.KillerSeat` keeps whose: the match) |
| `nearestEnemy/Blg(player)` | nearest to any living human |
| Orders to the player (`goto`, `follow`, `sit`, `attack`, ...), `control*`, `driveControl`, `controlDone`, cameras on the player, `cbFrom(player)` | one car: the match, else the lead. Orders to a human are still dropped outside a cut-scene |
| Orders at the player (`attack`, `guard`, `follow`'s target and reference, `evade`, `race`) | the living human nearest the ordered car, kept until another is 1.25× nearer (`ScriptHost.NearestHuman`, `Entity.CoopAim`); in a cut-scene the lead, which the scene drives (T01's intro has Taurus follow the player down the road: the nearest would be a teammate held at the start) |
| `teleport`, `teleportOffset` of the player | the lead, and the others into formation behind it moving as it moves (`FormUp`); also when the script lets go of the camera after a cut-scene that moved, drove or ordered a human |
| `destroy(player)` | the match; with none, every living human |
| Settings (`setSkill`, `setAgg`, `setAvoid`, `toggleAvoid`, `setMaxAttackers`, `startCar`, `setLights`, `setSiren`, `setHeliHeight`) | every human |

In the engine (`World.IsPlayerSide`, `ScriptHost.IsHumanEntity`), per human: the attacker cap and the tactic on deck
(`OnDeckNeeded`), the AI's blind radar (a human's jammer hides that human and blinds the AI at anything not a human,
as the one player's blinds it at all; an engine off hides its own car), the gun emplacements (each holds the nearest
human its radar may see, with the same hysteresis, and fires at them, or at the nearest living human with none in
sight; their rate gate draws as the original's), the T key's guns, Unlimited Ammo and Armour, the damage
model's player rules, the CB's speaker (a human speaks for the group: its lines go only when every human is dead).
`getdown` is refused (the typed cheats are the single player's own).

### The library routines run per human (`CoopRoutines`)

Recognised by their bytecode in every stock mission (`CoopRoutines.Analyze`, `CoopMissionTests`), not by name:

- **Leaving the map** (S01, S03–S05, S07, T01–T17, P01–P19): a thread per exit over (exit point, player): warnings at
  275 and 175 of the point (Nitro 150), at 50 (Nitro 60) `failAllObj(delay, 2)`, "abandoned", then the drive-off.
  Each such thread is seat 0's, and a copy for each other seat follows the program's threads (`Thread.Bound`): its
  warnings are `CoopLeaveWarning` events for that seat, and at the fail radius the human is put back beside a living
  teammate away from every exit (outside the last warning's radius), in the same car (no repair from driving off), and
  the thread starts over. The mission fails, as the original, only with nobody to come back to. A01, S02, S06 and B01
  carry the routine but run no thread on it.
- **Friendly fire**: `whoAttacked/Shot/Rammed(friend, v)`, a positive `isEqualId(v, player)`, then `inc(counter)`
  before the thread yields; the friend not an enemy. T01 data 12, T02 data 62, T03 data 46, T06 data 74 (the player's
  hits on Inferno: past 10 she drives off); Nitro B01, P01, P03, P04, P06, P07, P08, P19 keep the count in the routine's
  own local (with its 60 s decay).
- **Civilian kills**: a positive `isGroovesFault(victim)` of a non-enemy then `inc(counter)`: S04 data 45. (Nitro's
  civilian routines only taunt: no counter.)

A counter counts per human (`FsmVm._shadows`): the human the thread matched has their own count changed (with no
match, a reset, every human's), and the cell holds the highest, which the script's own limit is tested against. A
counter some routine also counts an enemy's death into is a goal and stays shared.

### Gates shut behind the player (`CoopGates`)

Some scripts shut a gate once the player is past it: a position test on the player whose branch runs `triggerGate`
before anything else is tested. With the test any human's, the first human through would shut it on the rest. The stock
missions' ones, recognised by their bytecode (`CoopGates.Analyze`, `CoopMissionTests`): **S06** nowayback, opened at the
start, shut at `isWithinSqNav(twopill, user, 10)` 60 m past it; **T07** gate1 (the abandoned road block), opened at the
start, shut at `isWithin(gateblock, groove, 600)` (both of its gates open again later, on a count); the Nitro trainer **B01**'s wall1 to
wall4, each opened by its lesson and shut at `isWithinNav(gateshutN, user, 25–50)`, just past it. (T10, T12, S07, P18 and
P19 move their gates on flags, timers, a helicopter's death or a computer car's arrival: those are single player's.)

In co-op such a test, once true for a human, holds the gate open while a living human is still on the near side of it:
the side of the gate's line (through its leaf, along its slide) away from the test's point, or less than 3 m past it
(`FsmVm.GateClear`). The test reads false meanwhile, so the thread waits where single player's would have gone on;
whoever drives through next counts, whether or not they touch the zone (S06's is 20 m across). With nobody behind it
shuts at once, as single player's. After 30 s of mission time (`FsmVm.GateHoldSeconds`) the ones still behind are brought
through, beside the human furthest past it, in the same car (`ScriptHost.BringThrough`, `CoopRespawned(seat, Gate,
beside)`; the server says so in the chat), and it shuts: a straggler who never comes holds the mission up 30 s at most.
A dead human is nobody to wait for: their respawn is beside the lead, who is through once it has shut. With every living
human back behind the gate the hold is dropped until someone passes again. A gate that is shut or sliding, a test on
anyone but the player, and single player are as the original. The hold (its test and since when) is in the savestate
(format 15).

### Respawns

A destroyed human comes back `RespawnDelay` (5 s) after the death in a fresh car beside a living teammate (the lead,
else the first living human), rebuilt in place as a network melee's reconfigured respawn (`MeleeGame.Rebuild`; the
driver's handguns kept and loaded, the engine started, `NetGeneration` up, the driver's keys at rest). With nobody
alive it waits: the script then sees the group dead. Seats' deaths and timers are saved.

### Co-op Scaling

The port's (`CoopSetup.ExtraEnemies`, `ScriptHost.ExtrasOf`, `CoopExtras.cs`; the host form's Co-op Scaling, `--coop-scale N`; off
by default). With N humans present, each enemy car (team 2; a helicopter or gun emplacement is left alone) gets min(N − 1, cap)
copies, the cap 1–3 (`CoopSetup.MaxExtraEnemies`), decided once, the first time the script orders it (most stock enemies are given
their sit or hide order on the first tick, so in a game that started from its lobby the copies are there from the start; a game
whose players came later has the copies of the humans present when each car was first ordered). A copy is the same car file
(an AI car's damage state, the night's lights) placed beside the original by the formation's rule (`SpawnSpace.Beside`), facing and
moving as it does, with its skill and aggression. Every order the script gives the original goes to its living copies in the same
tick, a human target or follow reference taken as the human nearest the copy (`NearestHuman`); its settings (skill, aggression,
avoidance, attacker cap, start, lights, siren) and a teleport (beside it again) too. Once the original is dead or gone, each living
copy attacks the human nearest it every tick until destroyed (`ScriptHost.ExtrasFrame`).

The script never sees a copy: no instance id (the registry's lookups miss it), no actor slot, and left out of the host's vehicle
lists, so `allEnemyDead`, `nearestEnemy` and `isWithinEnemy` and every objective and counter follow the originals alone
(`CoopScalingTests`: T02's objective succeeds when its three enemy cars die, their six copies alive, as without the option). A
copy that hits a human is still that human's attacker (`whoShot`). On the server the copies are spawn events with their files
(`CoopGroup.Spawned`), so the clients' mirrors build them as cars the server names; they are entities and cars of the savestate
(format 16: `Entity.ExtraOf`, `Entity.Scaled`, the cap in the build), and a restore makes them again (`CoopGroup.BuildEnemy`).

The base host form's Co-op Scaling row (a click steps Off, Up to +1, +2, +3) and CO-OP SCALING in the Nitro form's co-op game options set
the cap (shell.cfg [port] `coop_scale`, written only when on). The game's answer lists it after the makes and models
(`SessionInfo.CoopScale`; protocol 23) and the session popup says "Co-op scaling up to +2"; a host migration's successor has it in
its options.
The fresh car (a respawn's or a joiner's) stands still. **Co-op Rolling Start** (`CoopSetup.RollingStart`, the port's: the host
form's Rolling Start, `--coop-rolling-start`) gives it the teammate's speed when the teammate is driving along: on the ground,
1 m/s or faster (`ScriptHost.RollingMinSpeed`), not reversing and no more than 15 degrees between its way and its heading
(`RollingMaxSlip`); the car faces as the teammate does and goes along that heading at the teammate's speed over the ground
(`ScriptHost.Rolling`). Otherwise it stands still as before. It is part of the savestate's build. The base host form's Rolling Start row and
ROLLING START in the Nitro form's co-op game options set it (shell.cfg [port] `coop_rolling_start`, written only when on); the game's answer
lists it after the makes and models (`SessionInfo.RollingStart`; protocol 23) and the session popup says "Rolling start".

### Cut-scenes and the controls

A cut-scene (the script holding the camera) drives the lead; every driver's keys are held as the one player's were
(`SimSession.Cutscene`), so the others wait where they are until a teleport or the end of the scene forms them up
behind the lead. Which screen shows the cut-scene, and the gameplay cameras, are the presentation's. The scene's cars
go at the lead (above) and their way keeps clear of the waiting seats (the formation), so it plays as single player's
(`CoopFormationTests`: T01's intro, Taurus and the lead where single player has them; in the openings of T01–T03,
T05, T06, S01, P01, P03 and P06 with 2 and 4 humans no AI car drives into a seat in a cut-scene, nor a friend's at
all, in the first minute).

**Personal cut-scenes.** Not every camera scene is the story's: a short moment of one player's own (the jump cameras of S03,
T02, T05, T06, T10 and T12, the camera that follows a car driving off the map's edge) is that player's alone. `CoopCameras`
tells them apart by bytecode, and docs/coop-cameras.md has the survey of all 190 scenes of the 45 stock scripts (53 personal,
the rest story, 5 ambiguous ones global by default). A personal scene starts on the human the thread matched (the jumper),
the script's camera ops go to that seat's own camera (`ScriptHost.PersonalView`), their keys do nothing while it lasts (as a
single player's in a cut-scene: `SimSession.CutsceneOf`) and nothing else changes: the other humans keep their view and
controls, no one is driven or formed up, the AI does not stop. Its CB line (S03's) is that player's radio alone. It ends with
its script's `popCam`, its thread's end, the human leaving, coming back from a death or being put back after driving off the map,
or after 90 s. (The leave-the-map camera used to take the global camera when any human reached an exit, and kept it for good.)

**Rams.** The original names each car of a contact the other's rammer, whoever moved, so a friend
driving into the player is the player's ram to a friendly-fire routine (T01's Taurus: "watch it", and the count). In
co-op an AI car that drove into a human (its own speed into the contact, along the normal, the greater) does not name
that human its rammer (`DamageModel.CarContact`); the human's own record of the ram, and the damage, stand. Single
player keeps the original's rule.

### Co-op over the network (protocol 15)

```
nix develop -c dotnet run --project tools/I76.Server -- --coop t01 [--max 8] [--autostart] [--start-when N] [--port 7676] [--name NAME] [--password WORD] ...
godot-mono --path game -- --host-listen --coop t01 [--coop-lobby] [--max-players 8] [--port 7676] [--name N]
godot-mono --path game -- --connect HOST:PORT [--name N]
```

The server runs the mission and its script (`CoopMode`, an `IHostMode` beside `MeleeMode`; `ServerOptions.Coop` names
the mission, which is also the game's arena). The clients do not run the script: what a single player sees of it comes
from the server.

- **The seats.** The mission is built with a group of `MaxPlayers` seats (1–8; `--max` / `--max-players`, 8 at most, `Protocol.MaxCoopSeats`),
  each in the mission's own car until its player comes ("Each player's car", below). Seats nobody holds
  are out of the world (`ScriptHost.CoopVacate`: no car, no wreck). The first player takes seat 0, the script's player;
  each later one the lowest free seat, in a fresh car beside a living teammate (`CoopJoin`); a leaver's car is blown up
  and stays a wreck (`CoopLeave`), and a later joiner may take that seat. A join is refused when every seat is held, after
  the end, or (briefly) with nobody alive to come in beside. Seat 0's driver handguns are built in
  (`CoopMission.Build`, which the server, a successor and every client use alike).
- **The start.** (Without a lobby: `--autostart`, the command line's `--host-listen --coop`, tests; with one, below, "The co-op lobby".) The mission waits for its first player's first input (a game sends inputs once its mission is built),
  so the host's own game sees the opening cut-scene from its start. Later joiners come in where the mission is.
- **The mirror.** A client builds the mission as the server did, from the accept's seats (`CoopInfo.SeatCars`): the
  cars come out in the same order with the same serials, and its script host (never run) has the same entities, objectives
  and text. `CoopMirrorCars` hands the built cars out by serial as the server names them (the accept, spawn events); the
  others stay out of the world. Its `ScriptHost` is written from the server's events and snapshots, so the game's own
  HUD, radio, handset, notepad, script camera and end flow read it as in single player.
- **The radio** (`RadioEvent`, `RadioCutEvent`): each line as the server's radio puts it on the air (its queue and
  priorities stay the server's): the sound, the speaker (the mission's entity by index; a human's line is each client's own
  player's, so the handset rises; none for the radio), the priority. The client's radio plays it at once in place of
  whatever was (`CbRadio.Play`) and ends it by its length; a cut with nothing after it goes quiet. A **leave-map warning**
  (`CoopLeaveWarning`) is a radio event for that player's client alone (the server's event filter, `RadioEvent.To`).
- **Objectives** (`ObjectiveEvent`): an objective's flags when they change; the client's note sound plays as for a
  success or a reveal. The notepad (N) and the map (M) open over the running game in a network co-op game (no pause), the
  car held.
- **The script camera** rides in every snapshot's tail while it holds the view (the eye and what it looks at, or its
  basis): the client draws it between two snapshots as it draws the cars (a jump of over 40 m is a cut). Its own car is
  then drawn from the snapshots too (the script drives it; the prediction goes on underneath and takes over again), and
  the network HUD gives way to the scene. **Skip** is the host's alone: the server keeps it only from the host's seat
  (the listen host's, the one that took over, else the lowest: `InputQueue.AllowSkip`), and the game says the same
  (`NetHud.CanSkip`): there Esc (pad B) skips, on every other player's game it opens the menu (Leave Game, Exit Game) in a
  cut-scene as at any other time.
  A **personal** camera scene (above) rides in the same tail (protocol 19) as a list of (seat, camera): every client gets the
  list, shows its own seat's when the story's camera is not on, and marks the view personal (`ScriptCamera.Personal`), so its
  own car is drawn from the snapshots, the HUD gives way to the scene, and Esc is the menu (no skip).
- **Gates** the script slid (type-7 nodes) ride in the tail too, each off its place as (structure, node, how far along).
- **Movies** (`MovieEvent`: T05's and T06's `playMovie`, the only mid-mission ones in either game): the game **stands still for
  everyone** and every client plays it. The server stops ticking (`CoopMode.Holding`: no timer runs and nobody can be hurt), a
  client takes the movie from `MeleeClient.MovieName` the moment the event arrives, not with its tick, and neither sends inputs
  nor predicts, draws on or extrapolates (`NetView.Frozen`, `ClientWorld.Hold`; the music pauses as in single player). The movie
  is over when the **host's** copy is (`MsgType.MovieDone`, from the host's client when its movie ends or the host skips it:
  only the host's Esc, click or pad button skips, another player's Esc opens the menu, under the movie), or when every
  player's is, nobody is left, or after 10 min; the server then sends `MovieEndEvent`, every client's copy stops at once and
  the game goes on (the clients' view clock is taken afresh from the snapshots). A player who joins during it gets it in the
  join accept (`CoopInfo.Movie`); a client whose files lack it, or whose `--skip-movies` is on, answers done at once. Host
  migration: the successor takes the movie the clients are playing on (`MigrationHandoff.Movie`, or its replay reaching the
  `playMovie` itself) and the new host's copy ending ends it; a client whose copy was over answers again to the new host. The
  mission's own briefing (WRLD +0x04) is not played over a running game (see "The briefing", below). Before this the server did not
  stop: each client played the movie over the running game with its car held, a player could be killed under it, the clocks ran,
  every player's Esc skipped their own copy, a late joiner never saw it, and the script's teleport after the `playMovie` had
  already moved the cars.
- **Sirens** (`SirenEvent`; the script's `setSiren` or a police car's honk, the entity's, not in the car state). Lights are the cars' flags, in
  the snapshots already.
- **The end** (`MissionEndEvent`): won or failed and the failure's line; each client's game ends as single player's (the
  outro on a win; the end menu with the line, Leave Game and Exit Game, on a failure). The game over follows 5 s later;
  there are no rounds, and a host leaving after the end does not hand the game on. Everyone leaving ends it too.
- **Deaths and respawns.** A human's death is a `KillEvent` (the feed's line) and its respawn after `RespawnDelay` a
  `RenewedEvent` (the car rebuilt in place); being put back from the map's edge is a `***` line. The HUD's death card
  counts down to the car beside a teammate (no keys: it comes by itself). The scoreboard (Tab) lists the players with their
  deaths.
- **A late joiner** has the state in its accept (`CoopInfo`): the objectives, the sirens on, the radio's line and how far
  into it, the end if the mission is over.
- **Host migration** works as for a melee: the savestate (format 12) holds the mission, its script and the group; the
  successor builds the mission from the options (`ServerOptions.Coop`), restores, and replays the host's inputs; the seats'
  names come from the migration extras (`IHostMode.Restore(drivers, names)`).
- **Listing.** A co-op game is listed with its mission as the arena (`SessionInfo.Coop`; the anet tag's kind bits 3).
- **Co-op Damage** (`ServerOptions.CoopDamage`, `CoopSetup.DamageFactor`; the host form's, i26-server's and `--host-listen`'s
  `--coop-damage 0.5|0.75|1|1.5`): the humans' cars take that times every damage (`DamageModel.CoopDamageFactor`, each channel
  truncated); x1 by default. It is the server's alone (the clients take damage from the server) and part of the savestate's build,
  so a successor builds it again from the options. The base host form's Co-op Damage row (a click steps it) and CO-OP DAMAGE in the Nitro
  form's co-op game options set it (shell.cfg [port] `coop_damage`, written only when not 1). The game's answer lists it after the makes and
  models (`SessionInfo.CoopDamage`, in hundredths; protocol 23) and the session popup says "Co-op damage x0.5" when it is not x1.
- **Not done yet:** the trip campaign.

### Each player's car

A player's car is their own choice, or "the mission's own". The join carries it as for a melee (the car file, and its
configuration for a variant the server lacks), or `Protocol.MissionCar` (`*mission`) for the story's: the default of the
shell's forms (`ShellSettings.CoopOwnCar`). A melee server takes `MissionCar` for its default car, so a command-line join
with no `--car` is unchanged there and gets the story's car in a co-op game.

- **The server** (`CoopMode.Admit`): a car goes through the join's checks as a melee's does (`CheckJoin`: the melee model
  list, the garage's rules for a configuration, Hidden Weapons & Cars, the game's WEAPON RESTRICTIONS); `MissionCar` is
  checked against nothing. The first player's seat is in the world already in the mission's car: `ScriptHost.CoopRecar` makes
  it again in place (the vehicle, its entity and serial the same, as a respawn does; the driver's handguns of the new car),
  where it stands, before the mission starts. A later player's seat is built when they come (`CoopJoin`, from the seat's
  `CarFile`) and comes in beside a living teammate; the car a respawn builds is the same car. "The mission's own" for a later
  seat is the mission's (`CoopMode.OwnCar`, seat 0's car as the mission built it), not the first seat's choice.
  Reconfigure stays refused: a seat keeps the car it came in.
- **The accept** names each seat's car in `CoopInfo.SeatCars`, as the mission is built from them on a client (null: the
  story's own for seat 0, seat 0's for a later seat): every seat's current car (`CoopMode.SyncCars`; a seat nobody holds
  yet has the story's), so a late joiner's mirror is built with the cars the others came in, and its own car is the
  mission's player (or its seat) as built. An already joined client's mirror has the seat's car as it was built when it
  joined and takes the server's word for the one that came since (the spawn's car file: `CoopMirrorCars.Build` makes a
  car where the built one differs).
- **A successor** takes the savestate's `SessionBuild.Coop.Cars` before it restores (`CoopMode.AdoptCars`: each seat
  made as the old host had it), so the snapshot's cars are the mission's here and the build is the same.
- **The forms** (below) offer "Mission's own car" as the first row of the MAKE / MODEL list of a co-op game; any other
  pick (a model, a variant, CONFIGURE CHASSIS) is the player's own car and is kept as the setting, until "Mission's own
  car" is picked again.

### The briefing

Single player plays the mission's briefing movie after the loading card and before the mission starts. In a co-op game each
player sees their own (`Main.CoopBriefing`, from the loading card; Esc skips it). Since protocol 20 a game in its lobby plays it **at the start**,
after the accept, while the server waits for every game to load (below, "The co-op lobby"); only a player joining a mission already running plays it
before joining (a joiner would otherwise stand in a running mission under it; the roster's "co-op" against "co-op lobby" says which). The command line (`--host-listen --coop`, `--connect`)
and `--skip-movies` have none, as before.

### The co-op lobby (protocol 20)

Before protocol 20 the host of a co-op game started the mission at once (its first input) and everyone else came in to a mission already
running, their briefing played first. A hosted co-op game now opens in a **lobby**, where the players gather and the mission starts when
they say so. Nothing like it existed before in I26's net melee (a melee game has no room: a joiner is seated in the arena at once, as in the
original, whose NET MELEE·ENTRY ROSTER is the list of games to join), so the lobby is that roster form used for the players: the same art
(`DATABASE.MW2` record 18), car panel, MAKE / MODEL, VARIANT and CONFIGURE CHASSIS, in both looks (`CoopLobbyScreen`).

- **On the server** (`NetMeleeServer.Lobby.cs`, `ServerOptions.Lobby`): a connection that passes the join's checks (version, password, the invite
  answer, a full game, the car's `CheckJoin`) is not seated: it is a lobby player, with an id of the lobby's, its request, a ready mark and a chat
  limit, and the server sends everyone `LobbyState` (the mission, the seats, who is who, the players' cars and marks, the mission's own car, which
  id hosts) whenever it changes. No world is touched, so a leaver costs nothing: no wreck, no seat to give back. The lobby holds as many
  players as the game has seats (NO. DRIVERS).
- **The player's side**: `LobbySet` (the ready mark, and a car file with its .vcf and paint to change it: checked as a join's car, answered with
  `ReconfigureAnswer`; a new car takes the ready mark off), chat (`Chat` as in a game; the server relays `LobbyChat`), `Leave`. The client is in
  `ClientState.InLobby` with `MeleeClient.Lobby`, and `JoinAccept` arrives at the start.
- **The start.** A listen host presses START (`LobbyStart`, from the host's client alone; the host's in-process `NetMeleeServer.StartLobby` is the same):
  when every other player is ready the mission starts, else the screen asks "Start anyway?" and sends the forced start. A dedicated server has no
  host: it starts by itself when **every player there is ready and at least `--start-when N` (1) are**; the console's `start` forces it, and
  `--autostart` has no lobby at all (the first input starts the mission and later joiners come in where it is, as before). At the start the
  lobby's players are seated in the order they came (the host first, seat 0, the script's player), each in the car they chose, **all before any
  accept is written**, so every accept names every seat's car; then every accept goes out. The seats and cars exist from the first tick.
- **Loading together.** The mission does not run (`CoopMode.Holding`) until **every seat's game has built the mission and sent its first input**,
  or `ServerOptions.LoadSeconds` (120) after the start: the briefing (WRLD +0x04) plays in every game after the accept, the loading card up, then the
  mission is built, so the cars appear together. A game still loading at the timeout is left behind: the mission starts, its seat stays, and its
  inputs are taken from the moment it arrives, as a late joiner's would be (its car stands at the start until then).
- **Late joiners** after the start are seated as always (the accept, then their own briefing before it, if the roster showed the game in progress).
  A joiner that the roster showed in the lobby plays its briefing at the start.
- **The host leaving the lobby** closes it: "The host closed the lobby." goes to everyone in it and the game takes no more joins. The listen
  host's game is the server, and before the mission starts there is nothing to migrate (no savestate, no replay), so a new host cannot be handed
  the lobby the way a running game is (docs above, "Host migration"); a closed lobby sends the others back to the form. A guest leaving, or dropping, is just
  taken off the list. The host boots a lobby player with Delete (`NetMeleeServer.Kick`, the in-game boot's); they are told as a booted player is.
  The mission cannot be changed from the lobby (the server has built it).
- **Listing.** `SessionInfo.Lobby` (a byte at the end of the answer) and the anet tag's kind-3 goal nibble bit 3: the roster says "co-op lobby" in the row
  and the game popup, "co-op" for a game in progress, and counts the players in the lobby (its drivers are their names and cars).
- **The screen** (`CoopLobbyScreen`, a hosted `Shell` over the loading screen): LOCAL DRIVER is the player; the list holds the players, each with
  READY, the host's HOST, and the line above it the mission and "(players/seats)"; a click, Up or Down highlights one, and the car panel shows their car
  (the mission's own car shows its blueprint, "Mission's own car" in the MAKE / MODEL line, as the co-op form does). The highlighted player is the
  player's own row to change the car: MAKE / MODEL (whose list begins with "Mission's own car"), VARIANT and CONFIGURE CHASSIS (the garage)
  send it at once. The left button is READY / NOT READY (START for the host), CANCEL or Esc leaves. Under the list: what the game waits for and the
  chat (Enter types a line, Enter sends, Esc drops it).
- **Options.** The shell's host form always opens the lobby; `--host-listen --coop M` starts at once as before unless `--coop-lobby`; `i26-server
  --coop M` has the lobby unless `--autostart` (`--start-when N`). The bots (`i26-server --bots`) mark ready as soon as they are in.
- **Tests** (`CoopLobbyTests`): the new messages' round trips and the listing's flag; joining an unstarted game is the lobby, not the mission
  (nobody seated, the tick standing still, the listing in lobby); ready marks, a car change and its answer, chat, a guest's start ignored; the
  host's start with every seat dealt at once in its chosen car and the accepts naming them; a forced start; the first tick waiting for every game
  to load; a straggler left behind at the timeout; a late joiner after the start taking the old path; the host leaving closing the lobby with its
  message (and a later join turned away); a guest leaving; the host's boot; a full lobby; a dedicated server starting when enough are all ready
  (`--start-when 2`) and the console's forced start; and a game with no lobby seating at once.

### Hosting and joining from the shell

MELEE → MULTI MELEE → HOST (IPX or INTERNET) opens the host form (mode 3 or 4, above) with a way to make it a co-op game:

- **The Nitro Pack's form** has Co-op in its GAME TYPE list beside Combat, Race and CTF; **the base game's** form has a
  Game Type row, Melee or Co-op; a click switches.
- A co-op form lists the install's story missions by their titles instead of arenas (`MissionTitles.Coop`: the stock
  scripted missions, `MapFileSet.IsStockMission`: I76's A01 ("Training ground"), T01–T17 ("7. The Blood On The Highway": the 17 titles the original game holds; "Mission N" without the exe) and the scenarios S01–S07 by
  their shell names in the shell's own order; the Nitro Pack's campaign in SCENARIO.DAT's order, then B01 and the P
  missions SCENARIO.DAT does not list). It keeps the host form's GAME NAME (the mission's title until renamed), NO. DRIVERS
  (1–8), INVITE ONLY, the password, WEAPON RESTRICTIONS (Nitro's form) and Hidden Weapons & Cars, which apply to the cars
  the players choose; it has no TEAM PLAY, score or other limit, Bots or Share Maps (a stock mission is on every install).
  The base form leaves those rows out and the rest close up.
- The car panel shows "Mission's own car" (the default) or the player's pick (above).
- Hosting opens the lobby (above, "The co-op lobby"), where the host starts the mission. BROADCAST GAME hosts it as `--host-listen --coop MISSION --coop-lobby` does (`NetLaunch.Coop`, `MainNet.ApplyNetLaunch`).
- The roster (JOIN) lists a co-op game with a "co-op" tag after its name, "co-op lobby" while the host has not started it (the anet tag's kind bits 3 and the session
  answer's flag; protocol 20), its top line adds the mission's title when the name does not say it, and the game's popup (the arrow)
  shows GAME TYPE "Co-op" and the mission's title as AREA OF PLAY. Joining goes as a melee's (password, INVITE ONLY's wait; a
  stock mission needs no map transfer). With a co-op game highlighted the form's car is the co-op choice (above).

## How single player was checked

- **The old tick against the new.** The old `SimDriver` tick was emulated
  headless, one frame per tick, and compared with `SimSession` over
  1500–4000 ticks. The scenarios:
  - an m01 melee driven by every key: throttle, brake, steer, handbrake,
    fire, hardpoint, cycle, link, lights, reverse, and all four target keys;
  - m05;
  - t01, with its script;
  - an m01 melee with kills injected (eight waves) and two regen-pad
    visits.

  A per-tick hash of every car, the rounds, the rand seed and the score
  was the same on the library before and after.
- **Captures before and after.** The m01 fight and the cockpit with a
  raised arm firing its handgun were captured on both versions. The m01
  fight was identical until the first steer. Both versions fired the same
  number of handgun rounds.
- **Tests.** All the existing tests pass, the trace-equivalence ones
  included.

## Tests

`tests/I76.Sim.Tests/SessionTests.cs`:
- `CarInput`'s bytes, the command queue, the arms and the far policy.
- On the arenas (skipped without an install):
  - a 60 s network melee (two scripted humans, two AI cars) whose input log,
    replayed from its bytes into a fresh session, hashes the same on every
    tick;
  - a driver's car restored from a snapshot steps through 200 ticks
    (damage, weapon and light commands, reverse) exactly as before;
  - mass-scored kills and the respawn;
  - team play and the score limit;
  - the time limit and per-player regen pads.

## Playing over the network (Net-1)

Net-1 adds the network itself: a server-authoritative LAN melee. Clients
send only their inputs. The server simulates everything and sends back what
happened. Each client predicts its own car (protocol 13, "Prediction" below);
every other car is drawn as the server sent it.

### Running a dedicated server

```
nix develop -c dotnet run --project tools/I76.Server -- [--install DIR] [--game i76|nitro] --arena m05 --port 7676 \
    [--max 32] [--ai N] [--bots-ai N] [--bot-skill easy|normal|hard] [--bot-car N=MODEL[:VARIANT]]... [--teams] [--max-score S] [--time-limit SECONDS] [--name NAME] [--seed N] [--upload MBIT] \
    [--share-maps | --no-share-maps] [--map-max-mb 16] [--map-rate 256] [--rotate a,b,c] [--once] [--round-pause 15] [--password WORD]
```

- `--coop MISSION [--max 4] [--autostart] [--start-when N]` serves a co-op story mission instead of a melee (above, "Co-op over the network"): the players gather in its lobby and the server starts the mission when everyone there is ready and N (1) or more are; `--autostart` skips the lobby (the first input starts it); the console's `start` forces the start (above, "The co-op lobby").

- The server needs its own install. `--install` or `I76_INSTALL` names it;
  with neither it finds the game as I26 does (`GameDiscovery.Places`: the
  executable's folder and its parent, `i76/` and `nitro/` there and in the
  local user folder, then GOG offline installers, which it unpacks into the
  same places). With both games present it serves Interstate '76 unless
  `--game nitro`. It logs which game it chose. It builds the arena and every
  car from that install. The release zips carry it as `i26-server[.exe]` next
  to the game.
- `--ai N` fills the arena with N AI cars, which attack the nearest human.
- `--bots-ai N [--bot-skill easy|normal|hard]` seats N bots, players driven by the server that play the game's mode (below, "Bots"); they take the seats free
  under `--max` and leave as humans fill them. `players` lists them with "bot".
- `--bot-car N=MODEL[:VARIANT]` (repeatable) picks the car of bot seat N (1 is the first; "A.I. cars" below): MODEL is a model's file stem (`vppirna`, `vcgcour`; the
  Nitro Pack's are the same stems), VARIANT its number (1 by default; the host's saved variants count), or `N=vppirna2.vcf`, or `N=random`. `--bot-cars-file PATH` reads
  the same as lines `N=MODEL[:VARIANT]` (`#` comments). A seat with no entry rolls. Examples: `--bots-ai 3 --bot-car 1=vcgcour:2 --bot-car 3=vdstag3`.
- `--max N` seats up to 64 players (32 by default); `--upload MBIT` limits the upload the game uses, each client's
  snapshots taking its share (below, "The snapshot budget"; "How many players" for what each size needs).
- For testing: `--bots N [--bots-lag ms[,loss%[,jitter]]]` seats N headless players from the server's own process (half
  hunting the nearest car, half roaming), through a lag proxy if given; `i26-server --bots-to PORT --bots N` runs only
  the players, joining a game on this machine's PORT (a listen server's). In a co-op game they play as teammates
  (`BotDriver`): each hunts only the cars of the enemy team (not its own, not team 0's bystanders, by the `CarInfo` the
  server answers), drive in a column in seat order while no enemy is near, each 15 m behind the one ahead along the way it
  drove, the first behind the nearest ally (T01's Taurus: four of them keep up with him and finish its first objective; they cannot blow up the
  fireworks stand, a structure, so the mission fails there). A stuck bot backs off after 2 s hardly moving.
- Every 30 s with players seated it logs a load line: players, the tick's time, the upload, the snapshot size and how
  many went in pieces (a listen server's log too, as `[server] load:`).
- It logs joins, leaves, kills, scores and respawns. A line typed on stdin is sent as the server's chat, except the
  console's commands (a leading `/` optional): `kick NAME|SLOT` boots a player, `ban NAME|SLOT` / `unban NAME` / `bans`
  (below, "Bans"), `players` lists them, `y` / `n` / `s`
  answer an invite-only join (`--invite-only`), `say TEXT` sends a line that starts with a command's word, `help`.
- Ctrl+C stops it. When a melee is over it carries on with another round
  (below, "Rounds"); `--once` exits instead, after 20 s for the clients to see the standings.
- The port is UDP. Open it in the firewall for a LAN game.
- The game's rand stream (spawn points, AI choices, damage rolls, debris) is
  seeded from the clock, as the original's `srand(time(NULL))`. `--seed N`
  fixes it, so a melee can be replayed; the seed is in the first log line.

### Rounds

A melee that ends is not the end of the server (protocol 12). `NetMeleeServer` (with `ServerOptions.Rounds`; the console
server unless `--once`, and the listen server always) goes on like this:

- **The pause.** The game over goes out as before, now with the pause in it (`GameOverEvent.NextRoundSeconds`,
  `--round-pause`, 15 s). Clients keep the final standings on screen with "Next round in N s". The listing (`--anet`) shows
  the game as over, not joinable, meanwhile.
- **The next round.** It is a new `MeleeHost` (so scores, cars, rounds in flight, the destroyed structures and the rand
  stream start again; the seed is the last one plus one, so `--seed` still replays). Its arena is the same one, or with
  `--rotate a,b,c` (the names `--arena` takes, custom maps by their file name) the next of the list, wrapping; a server
  started on an arena that is not in the list goes to the list's first. A race or capture the flag arena in the list brings
  its own kind, and a goal given for another kind is dropped. If a rotation entry cannot be built the same arena is played
  again, and if that fails the server ends. The listing is updated with the new arena and player count by itself
  (`NetMeleeServer.Describe` reads the current host).
- **The seats.** Each client still connected is seated in the new host as it first joined (name, car, its car's
  configuration, the team it holds), in its old slot order. The server sends it `NextRound` (reliable, with the arena), then
  what a fresh join gets on the same connection: the accept, or for a custom map the map offer and the same
  download/prompt, with the seat held (`Waiting`). The client (`MeleeClient`) drops the old round (accept, snapshots, events,
  scoreboard) at `NextRound` and raises `NewRound`; the old round's mirror takes nothing more from the connection (a
  `ClientWorld` reads only while the client's accept is the one it was built from), since its scene runs a few frames
  more before the reload; the game keeps its `NetSession` (the connection, and a listen server) in
  a static across the scene reload and joins by it on the loading screen, which asks about a missing custom map as for a first
  join. Nobody goes back to the shell.
- **Leaving.** A player who leaves during the pause (Ctrl+F10, then the shell's STANDINGS) just drops out and is not carried.
  A listen host leaving hands the game to a player (below, "Host migration"). The host can press Enter on the
  standings to skip the wait. A client whose server goes away during the pause returns to the shell's STANDINGS with a notice.
- **An empty server holds its round**: with nobody seated or arriving, a carrying-on server does not step the game (no clock, no AI), so an idle server neither plays rounds to nobody nor rebuilds arenas, and the first joiner gets a whole round.
- **Joins during the pause** are refused ("between rounds: try again in a few seconds"); the next round is joinable
  from its start, by the usual mid-game join.
- Not done: a vote on the next arena, per-round settings.

### Host migration

A game hosted from the game (a listen server, `ServerOptions.HostMigration`) goes on when its host quits or drops; a
dedicated server going down still ends its game. The original did the same in its own way: its peers carried on and the
lowest remaining player id became the host ("*** %s is now the host!"; docs/deviations.md). Here
(`src/I76.Net/HostMigration.cs`, `MeleeClient.Migration.cs`, `src/I76.Server/NetMeleeServer.Migration.cs`,
`MeleeHost.Migration.cs`, `MigrationStandby.cs`; protocol 13):

- **The successor** is the lowest seat, bar the host's own (`NetMeleeServer.MigrationOwner`), whose client said at its
  accept that it can host (`HostCandidate`: every game client with an install; bots and tests' bare clients do not). The
  host tells every client who it is, where it is (the address the host sees, then its own addresses at home) and the
  client's own seat token (`Successor`), again whenever the seats change. Nobody negotiates: everyone has the host's word.
- **Keeping it in step.** Only the successor is sent anything more, on the map channel (which only clients not yet seated
  use): a savestate every 1.5 s, at once after a join, a leave or a new round (`MigrationState`: the session's snapshot,
  format 8, with the host's extras: the options, the round, each seat's token, request and addresses, the invite-only
  joins waiting, the anet servers, the cars served by content and the shot counts), in 1100-byte pieces at most 24 KiB/s
  (a quarter of its share with `--upload`), and every tick's inputs as the session applied them (`MigrationLog`, the
  slots that changed). The stream counts against the successor's snapshot budget as its events do. On m01 with four
  players a savestate is 4 KB: about 3–4 KB/s to the successor, nothing to the others.
- **The host gone**: its goodbye (the disconnect's reason `HostMigration.HostLeavingReason`, at once) or 3 s without a
  message from it. The successor's client lets its game socket go; `MigrationStandby` (which built the arena when the first
  savestate came) restores the savestate, replays the host's inputs since (the game is deterministic: this is the host's
  game, `TheSuccessorsReplayIsTheHostsGameExactly`), holds each driver's last keys up to 0.3 s past the newest tick it had
  seen (no client sees a tick twice), and listens on that same port, so the address the host and the others knew, and its
  NAT mapping, still lead there. A join or leave after the last complete savestate is a barrier the replay stops at
  (a rollback to that savestate, up to 1.5 s).
- **The others** probe the successor's addresses (`I26M` unconnected messages with the game's token) and the new server
  probes theirs (the addresses the old host saw), so an endpoint-independent NAT on either side opens as in the hole
  punch; whoever answers first is joined with the seat's token (`JoinRequest.Rejoin`). They come back to their seat, car,
  team and score; the client keeps its accept and input clock, drops the old connection's snapshots and decoder, and the
  mirror takes the new host's cars (`ClientWorld.Rejoined`). The restored game waits for them (all back, or 3 s), then the
  old host's player leaves as any leaver (its car blows up); a seat nobody comes back to in 10 s is given up the same way.
- **anet.** The record of a game is the host's own to publish (its `MYSESSIONS` table; the server drops it with the
  host's link, and anet's master election belongs to dp sessions I26 does not use), so the successor lists the game anew
  on the same servers under the same name. Nobody needs the lobby to come back: the addresses came from the host.
- **Invite only.** A join waiting for the host's answer is told the successor too; it waits for the new host's answer
  instead, keeping the time it has waited.
- **Bots** (docs above, "Bots") ride in the savestate with their brains and slots; the successor's game has them, and the options it was built
  from say how many to keep.
- **A quiet host that speaks again** before anything was done (the player's own link had dropped) keeps its player.
- **The feed**: "*** Migrating host..." (ours), then the original's "*** NAME is now the host!" or "*** You are now the
  host!", and the old host's "has left the game" from the new host. A migration that has not seated a player in 10 s
  ends the game for it as before ("connection to the host lost").
- **Measured** (the tests, one machine): a clean quit has everyone back in 0.1–0.2 s; a dropped host in 3.1–3.3 s; the
  restore and replay 30–120 ms. Three game instances end to end, the host killed: back in 3.2 s.
- **Not covered**: symmetric NATs (as the hole punch); a successor whose own link dropped takes over alone (the others
  stay with the host, which it no longer hears); a game on a downloaded custom map hands over only if the successor's
  game finds the map (`MigrationStandby.MapDir`); joins still downloading a map are lost; a migration in the standings
  pause shows the game over again.

### Joining from the game

```
godot-mono --path game -- --connect HOST:PORT [--name N] [--car X.vcf] [--team T]
```

- The server chooses the arena and answers with the car it actually built.
  A car file it does not have becomes its default car, the Piranha.
- Esc opens the menu (Leave Game, Exit Game, Graphic Detail, Audio Control, Control Configuration, Resume); the game runs on under it and the car is held neutral. Ctrl+F10 or Ctrl+Q also leave. There is no pause, and the
  map and notepad are off, because they would pause the game.

### A listen server

```
godot-mono --path game -- --host-listen [--arena m05] [--port 7676] [--fill N] [--bots-ai N] [--bot-skill easy|normal|hard] [--bot-car N=MODEL[:VARIANT]]... [--teams] \
    [--max-score S] [--time-limit SECONDS] [--name N] [--car X.vcf] [--max-players 8] [--upload MBIT] [--game-name NAME] [--seed N]
```

- This runs a `NetMeleeServer` in the game's process, on its own thread,
  and joins it through 127.0.0.1. Other players join with `--connect`.
- The host is an ordinary client of its own server: its car is predicted
  like any client's (over the loopback the server is a tick or two behind
  it), and the other cars are drawn 100 ms behind.
- A listen-server host could still cheat on their own server. Dedicated
  servers are the trusted option (the plan's Net-3 marks which is which).

### From the main menu

MELEE → MULTI MELEE → HOST or JOIN → IPX or INTERNET, the original's path. MODEM, NULLMODEM and OTHER are
not shown. The port's two transports:

| Entry | HOST (record 19, NET MELEE·HOST EVENT FORM) | JOIN (record 18, NET MELEE·ENTRY ROSTER) |
|---|---|---|
| IPX: the LAN (labelled LAN in the modern look) | mode 3: a listen server on port 7676 (the next free one up to 7679 if it is taken), which answers LAN searches | mode 7: the games a LAN search finds, asked again every second |
| INTERNET: a lobby server, or an address | mode 4: the address book first; the entry's host is the anet game server the game is listed on (below, "Lobby servers"), its port, if it gives one other than anet's 21157, the one to listen on (`server`, `server:7680`; `:7680` lists nowhere) | mode 8: straight to the roster, which searches **every entry of the address book at once** (below, "The internet roster"): the I26 games each anet server lists (`server`, `server:21157`) and a game at an entry's own address (`host`, `host:port`), asked every second, grouped by entry; SERVERS opens the address book |

**The address book** is the original's CONNECT TO SERVER popup (bank 36
sprite 29) over the form. It lists `user://internet.lst` (the
original's layout: `u32 n`, then n × {32-byte name, 64-byte address}), three
entries at a time (the Nitro Pack's art, four). A click picks an entry and shows it in the NAME and IP
boxes, which edit it in place. NEW adds an empty entry and edits its name,
then its address; DELETE removes the entry picked. The Nitro Pack's art has
DELETE; the base game's has DONE, NEW and CANCEL only, so the port redraws
that row as four narrower buttons, DONE / NEW / DELETE / CANCEL
(`AddressBookPopup.DrawBaseButtons`, `ArtButtons`): the art's DONE face
without its word at each place, the art's own DONE, NEW and CANCEL cut out
and centred, and DELETE lettered as the art letters its buttons (below,
"Buttons the art lacks").
HOST → INTERNET opens it first: DONE uses the entry picked; with none, or
with CANCEL, the form goes back to the main menu, as the original's did.
The internet roster opens it from SERVERS: DONE and CANCEL both go back to
the roster (DONE on an entry scrolls the roster to it). The book is saved
either way. A player without an internet.lst starts with the community's
anet servers (`AddressBook.Defaults`, below); an existing book is theirs
and is never changed.

**The internet roster** (mode 8; `ServerBrowser`, `src/I76.Net/ServerBrowser.cs`). The original's JOIN → INTERNET
connected to the one server picked in the address book and listed its games; I26 goes straight to the roster and searches
the whole book (docs/deviations.md, "MULTI MELEE"). Each entry gets its own `SessionBrowser` (its own socket, its own anet
link, its own name lookup in the background), so a server that is slow, silent or not found holds up none of the others, and
a game is always joined through the server that listed it (its `AnetListing`). An entry is read as before
(`ServerSource.Parse`): `server` or `server:21157` is an anet server, `host:port` with another port a game at that address,
`host` alone both. The list has a **header row per entry**, in the book's order: the entry's name in the label face (the art's
headings', LOCAL DRIVER's), and on the right its state in the handwriting: "2 games", "1 game", "no games", "connecting..."
(for up to 6 s, `ServerBrowser.AnswerWait`), "not answering", "not found" (the name has no address) or "no address" (the entry
is not one). The entry's games follow under it, as the rows below; a header row is not joinable (a click or JOIN GAME on it does
nothing; the highlighted game stays). A game listed on two servers is under both. The box above says "Contacting servers..."
while every entry is still being reached, "No servers in the book" for an empty book, else "No games found" when there are no
games. **SERVERS** (the port's, under the list on the line of JOIN GAME and CANCEL: hotspot 39, (42, 453)–(183, 471)) is the
art's CANCEL face drawn again without its word, and SERVERS lettered as the art letters CANCEL (below); it opens the address
book, and when that closes the roster searches the book as it now stands: the entries kept go on where they were (no new
login), new ones start, deleted ones stop (`ServerBrowser.Watch`).

**Buttons the art lacks** (`ArtButtons`, `ShellFrame.Label`, `ShellGlyphs.LabelPixels` / `Label`). The art letters its
buttons (JOIN GAME, CANCEL, DONE) in an antialiased bold Arial-like sans that no shell font record has. A port's button is
made from a button of the same art: its face, with the label left out (the ends as they are, the middle tiled from the plain
columns before the word), at the new size; the art's own word where it has one, cut out and centred; a word it lacks is set
in Arial Bold like the art's label beside it, measured at run time from the art (its ink's bounds and colour, by brightness
inside the border). In the 1997 look it is printed at the pixel size that prints the art's word closest to the art's own
(hinted, as the art's were) in the core's ink and blended into the palette; in the modern look the text layer draws it at the
window's resolution, as tall as the art's word (less its faint antialiased rim), as wide as the two words' proportions make
it, in the ink the art's strokes show in on the whole. Both are drawn into the frame, over an upscaled background or popup
too. Without Arial, the shell's label face (font 0x47), doubled.

**The host form** (the original's modes 3 and 4). The base game's is drawn by the port (`HostFormLayout`, `HostSheet`): the
original's form (record 19) had room for its own fields only, and the port's options had come to share four short lines under
its options box. It is a ruled sheet in Mouse Options' style, in both looks (the 1997 look's rules, title and buttons in the
palette, the modern look's as vectors), a labelled row for each field: on the left LOCAL DRIVER, Game Type, AREA OF PLAY with
its six-row list and arrows, GAME NAME, NO. DRIVERS, TEAM PLAY, INVITE ONLY, MAX SCORE (On / Off, - and +), Password, Bots,
Bot Skill and A.I. Cars (a co-op game has Co-op Damage, Co-op Scaling and Rolling Start in their place), then Ignition Lock and
Turrets, with rows to spare; on the right Sim Rate, Share Maps, Hidden Weapons & Cars, Unlimited Turrets, Car
Reset, CB Radio and Chase Crosshair, then the car: MAKE / MODEL, VARIANT, its weapons and parts and the blueprint's side view;
CONFIGURE CHASSIS, BROADCAST GAME and CANCEL are its buttons. Every row keeps the form's hotspot id, so a click (the row, or
its - and +), the pad's regions and the text entry do what the original's controls did. The Nitro Pack's form keeps its art.
- LOCAL DRIVER (RENAME), the area of play (the 15 arenas, as the instant
  melee lists them), GAME NAME (the area's name until RENAME gives it one; left
  empty it is the area's again);
- NO. DRIVERS 1–`MeleeScreen.MaxDrivers` (4 to start; the original's went to 8; the form's top is 32 for now, `Protocol.MaxSeats` = 64 is
  what the wire, the screens and i26-server take): the server's seats (below, "Bandwidth"). The form has no room for a note on the upload a
  big game needs, so `MeleeScreen.UploadMbit(players)` (a small table, placeholders until measured) goes to the log line a listen server writes;
- BOTS (the port's: the Bots row's `-` `+`, Bot Skill's Easy, Normal or Hard on a click; Nitro's form has them in its GAME OPTIONS):
  how many players the server drives (0 to the seats NO. DRIVERS leaves besides the host's own) and how well (below, "Bots"; kept in melee.cfg);
- TEAM PLAY, INVITE ONLY, and MAX SCORE with its ⊟ ⊞ (1000–100000 in steps
  of 1000, 20000 to start; the score counts only when its box is ticked);
- the car: MAKE / MODEL, VARIANT and CONFIGURE CHASSIS, as on the instant
  melee form;
- BROADCAST GAME: CHOOSE A TEAM first with team play, then the launch: a
  listen server in the game's process with these rules (no AI fill, no time
  limit; the form's bots), joined at once.

**The roster** (modes 7 and 8):
- each game found as its name, `(players/seats)` at x 160 and the arrow
  (bank 36 sprite 14) at x 204; "No games found" in the box above when there
  are none; on the internet roster under its entry's header row (above, "The internet roster");
- a row picks the game; its arrow opens the game's popup (bank 36 sprite 13):
  GAME NAME, GAME TYPE (LAN or Internet), SCORE LIMIT ("None"), TEAMS,
  AREA OF PLAY, and each driver's name, make and model, and team ("N/A"). The list shows eight drivers; with more the wheel,
  Up/Down and PgUp/PgDn page through it and "9-16 of 40" shows where it is. The game's answer to a search is one datagram
  (`SessionInfo.AnswerBytes`, 1300 bytes), so the driver list is cut to what fits after the rest (about 50 drivers with short names;
  the counts, `(players/seats)`, are always the game's);
- the row's count is right-aligned to the arrow (`(64/64)` is wider than the original's `(8/8)`; the name gives way);
- JOIN GAME: "There are no games to join." without one, "That game is full."
  or "That melee is over." when it is; CHOOSE A TEAM if it plays teams; then
  the launch, the game connecting behind the loading screen.

**CHOOSE A TEAM** is the original's popup (bank 36 sprite 19).
A team is 1–8, every row a pick (the eighth, MEN IN BLACK, is `Entity.Team8`). The popup lists teams, not players, so it is the same
for 64; CTF puts a joiner on the team with the fewest players, whatever the count.

**The launch.** `ShellLaunch` (kind 5, Multiplayer) carries a `NetLaunch`:
the port, game name, seats, team play, invite only and score limit to host,
or the address to join, and the team. Main turns it into what
`--host-listen` or `--connect` would give and starts the game the same way.
A host or join that fails goes back to the form with a notice saying why.

**After the game.** Eight seconds after the game over (the HUD shows the final
scores meanwhile), when the server goes away, or on Ctrl+F10 or Ctrl+Q, the shell comes back
on STANDINGS (record 28) with every player's row from the last scoreboard,
best first. The sheet holds 16 rows; with more players the wheel, Up/Down and PgUp/PgDn page through it ("17-32 of 61" beside DONE). DONE goes back to the form. A server lost before the end also
says so in a notice.

**The car** is the form's model and variant. The server builds cars from its
own install, so a variant the server's install does not have (CONFIGURE
CHASSIS' own `<stem><n>.vcf`, or one in the player's install addon\) goes with
the join as its .vcf (the request's configuration blob). The server checks it
with the garage's rules (`CarConfigCheck`: the model's own body, parts and
guns the install has in slots the body has, armour no more than the model's
stock variants, DONE's checks) and refuses the join with the reason if it
fails; a good one is built under a name of its own (`n<hash><stem>.vcf`), and
the cars the other clients are told of carry the file (`CarInfo.Vcf`), so
everyone sees the car that was picked. A join with no configuration for a car
the server lacks plays as the model's first variant. A car the server's install has on disk rather than in its archive
(addon\: a community car, the stock vmxmarx3, a stock car overridden there) goes out the same way, by its content with
its file, since other installs may lack it or hold another file of that name: a listen host playing one used to be a car
no joiner could build, so nobody saw the host. A listen server ignores
the process's overlay, which holds the host's own saved variants.

### Finding games

A server answers queries outside any connection (LiteNetLib's unconnected
messages; `src/I76.Net/Discovery.cs`):

| Message | Way | What |
|---|---|---|
| query | anyone → server: a LAN broadcast to ports 7676–7679, the same at 127.0.0.1, or one address | `"I76Q"`, protocol version |
| session | server → asker | `"I76S"`, protocol version, session id (random per server), game name, arena, players, seats, team play, invite only, score and time limits, whether it is over, each player's name, car and team, the Nitro kind, goal and team count, a custom map's hash, whether it is shared and its size, whether the game is locked (protocol 12; the password itself is never sent), ...; protocol 22 ends with the arena's name and each listed player's make and model where the host says it (below) |

**The names on the roster** (`SessionDriver`, `NetLines.GameName`, `NetLines.AreaName`). The game popup's MAKE / MODEL is the car's
make and model as the original's shows it (its body's VDFC name, "Courcheval Courchel"), read by the asker from its own files: the host
lists an install's car by its file (vppirna3.vcf) and a car it serves by its content (a player's own variant, a bot's car with weapons
taken off; served as n&lt;hash&gt;&lt;stem&gt;.vcf, a name that means nothing to a player) by its body (vppirnha.vdf). Only for a body that
is not the install's archive's (a community car) does the host add the make and model in its own words, in a list after the tail (a
string per listed driver, empty for the others), which the asker uses when its own files do not name the car. A co-op seat in the
mission's own car (`*mission`) reads "Mission's own car". The STANDINGS read a served car's body from the file the join accept carries.
The answer also ends with the arena's name as the host's lists give it (its AREA OF PLAY: the base game's table or the file's WRLD
name; a co-op game's mission title), since an asker without a custom map has no other. The popup's AREA OF PLAY is the host's name for
a custom map and the asker's own for the rest; a game listed with no name, or with a mission file's name, is shown by its area's name
(a host given no name lists it so itself), never by the file.

`SessionBrowser` asks and collects the answers, one per game by its session
id. A game that stops answering drops out after 3.5 s. Answers of another
protocol version are not listed. A game in progress keeps answering, so a
server is found whenever it is up. The dedicated server answers too, so it
shows on a LAN roster. An address given by name is looked up once, in the background (`SessionBrowser.Ask` sends nothing
until it is found, and `NotFound` says when it was not), so a slow name server never holds up the form. The internet roster
runs one `SessionBrowser` per address book entry (`ServerBrowser`, above).

### Game passwords

A game may have a password (protocol 12). Blank is an open game; the password is never saved (not in shell.cfg or any
file), so a host sets it again each time.

- **Hosting.** The base host form's Password row (None or Set) and the Nitro form's PASSWORD button open a small
  prompt (`PasswordPopup`, the notice's blank sprite with a line to type in through the shell's text entry; Enter or OK
  takes it, Esc leaves it as it was, an empty entry clears it). `i26-server --password WORD` and
  `--host-listen --password WORD` set it from the command line (`ServerOptions.Password`).
- **The join.** The join request carries the password after its configuration blob. `NetMeleeServer.CheckPassword`
  runs after the protocol check: a missing one is refused with "This game needs a password" (`Protocol.NeedsPassword`),
  a wrong one with "Wrong password" (`Protocol.WrongPassword`), compared in constant time. A wrong password from an
  address blocks that address's next try for 1 s (`Protocol.PasswordRetrySeconds`; the refusal says "Too many tries -
  wait a moment", whatever the new password), so guessing is slow. Asking with no password, to learn that one is
  needed, does not count as a try. The check comes before the map offer, so a custom map or a mid-game join goes on
  as before once it passes. A protocol-11 request has no password field: it still reads, and is refused for its version.
- **Listings.** A locked game shows a padlock in the join list. LAN discovery answers carry a locked flag (appended to
  the session answer; an older reader stops before it). For anet the flag is bit 5 of the session tag's byte 13
  (`I26SessionTag.Locked`). The record's own password field stays empty, because the lobby server stores and
  lists it in plain text to everyone. The real anet 0.10 source (`h/anet.h`: "Not supported yet."; `dp2.c`'s
  INDIRECT_JOIN handler) never reads `szPassword`: it looks the session up, and has the host open a connection to the
  joiner, so a dummy there would have been harmless, but a user byte needs no such trust and works on any server.
- **Joining.** JOIN GAME on a locked game (by its flag, or by the refusal of an earlier try: `MeleeScreen.KnownLocked`)
  asks for the password first, then CHOOSE A TEAM. A refusal for the password goes back to the form with the notice
  ("Could not join: Wrong password."); the next JOIN GAME asks again. `--connect HOST:PORT --password WORD` and
  `--join-anet HOST --password WORD` do it from the command line.
- **Tests.** `PasswordTests`: the request's encoding, right, wrong and missing passwords, the retry delay, the flag in
  both listings (the LAN answer, and through the fake or real anet server, which also joins with the password), and a
  protocol-11 client refused with its reason.

### Custom maps

The original never sent a map: a joiner whose copy of the host's mission file differed was told so and dropped, one
without it joined a mission it could not load. I26's host sends a custom arena to a
player who lacks it, or has another version of it.

**What a custom map is** (`MapFileSet`, `src/I76.Formats/Mission`). The arena's mission file
(`GameInstall.FindMission`), its terrain (`MissionWorld.FindTerrain`, ZONE's name or the stem's) and the loose files its
WRLD names (the `.npt` text, its palette, luminance, translucency, scrounge, horizon, the WDEF names, and the mission's
own `<stem>.npt`), each only if it is not stock data and lies where maps are put (addon\, a folder in addon\, miss16\,
beside the mission). Stock is a name in the install's archive (the ZFS and its paks) or one the stock installs ship loose:
the missions a01, m01-m15, s01-s07, t01-t17, b01, p01-p19 and the network arenas n01-n65 (with their `.ter` and
`_16.pcf`), and addon\'s i2ayj_13.map, fullres.lst and scenario.dat. A stock mission is no custom map: nothing is
offered, the join goes as before (the CRC fingerprint of protocol 8 still checks it). A custom map naming a stock terrain
sends its mission file alone. Every name must be a plain file name (letters, digits and `_-!#$&@^~`, at most 32 before
one of the map extensions: the seven mission kinds, `.ter`, `.npt`, `.act`, `.lum`, `.tbl`, `.sdf`, `.hzd`, `.map`,
`.tab`; no device names). A pack map (canyonlx.rac, addon\canyonlx.ter) is two files; the 186 KiB test map three.

**Its identity**: SHA-256 over `I26MAP1`, the mission's name and each file's name, size and SHA-256 in name order (case
folded), so the same files give the same hash in any folder. The join accept carries it (`JoinAccept.MapHash`), the
session answer (`SessionInfo.MapHash`, `MapShared`, `MapSize`) too. The anet listing has no room for it (its 14 user
bytes are full): byte 12's bit 7 says the arena is a custom map, byte 13's bit 4 that it is shared.

**The join** (protocol 11). For a custom arena the server, instead of seating a client as it connects, sends it the
offer and holds its seat (it counts toward the seats):

| Message | Way | Delivery | What |
|---|---|---|---|
| map offer | server → client | reliable ordered | the mission file's name, the hash, shared or not (and why not: "the host does not share maps", "the map is too big to send"), each file's name, size and SHA-256 |
| map request | client → server | reliable ordered | the hash and, per file still missing, its index and the offset to send from |
| map chunk | server → client | reliable ordered, **channel 1** | a file's index, an offset and up to 16 KiB |
| map ready | client → server | reliable ordered | the hash: the client has the map; the server seats it and sends the join accept |

A client refuses an offer whose names are not plain file names, that lists one twice or more than 16, or whose hash does
not cover its list. It then decides (`MapCache.Choose`): its install's own copy if it gives the same hash; else a checked
download of that hash in its cache; else a download if the server shares the map and it is within its cap; else the join
ends with the reason ("The host's map X is not installed, and the host does not share maps."). The game asks first, on
the loading screen: "The host's map X (187 KB) isn't installed. Download it?" (or "Your copy of the host's map X differs
from the host's"), **Download**, **Always** (which sets Play Options' **Map Downloads** to Always) or **Cancel** (Esc).
Map Downloads (`[port] map_downloads`): Ask (the default), Always, Never; `--map-downloads ask|always|never` for one run.

**The download** goes over the game's own LiteNetLib connection on channel 1 (both ends open two channels), so its
reliable queue never holds up the game's events. The server sends each downloading client at most 256 KiB/s
(`--map-rate`), all together at most 1 MiB/s, and stops filling a client's channel while 64 packets wait in it: players
already in the game share the line. The loading screen shows "Downloading the host's map X: 45% (84 of 186 KiB). Esc
cancels." Pieces are written in order into `user://downloaded_maps/<hash>.part/` (each checked against the offer: its
file, its place, nothing past the size); once every file has its size and SHA-256 the folder becomes
`user://downloaded_maps/<hash>/`. A download cut off (cancelled, the connection lost) leaves its part folder, and the next
join asks only for the rest. Nothing is ever written into the game install.

**Playing from it.** The downloaded folder is that session's `GameInstall.SessionMapDir`: every install the game opens
meanwhile searches it before addon\ for missions and terrains, and the VFS puts its files above addon\ (`GameInstall.MapDir`),
so a different local copy of the same name is not used. It is cleared when the game ends; downloaded maps are not listed
in the shell. A server never plays from it (`MeleeHost` opens its install with no map folder).

**The host.** The host forms' **Share maps** (the Nitro Pack's GAME OPTIONS row, the base form's Share Maps row;
`[port] share_maps`, on), `--share-maps`/`--no-share-maps` on `--host-listen` and i26-server. A map over 16 MiB
(`--map-max-mb`) or 16 files is offered unshared ("too big"). The server reads the files once at start and sends those
bytes (hashed again if they changed in between). It logs the map at start ("custom map xfermap.lvl: 3 files, 186 KiB,
hash 2dec5b2a671f2354; shared with players who lack it"), each download and its end.

**Cases.**
- Several clients download at once, each at its own rate within the total.
- A client joining a game in progress downloads first and is seated after; it is not in the game meanwhile.
- A client that does not answer the offer in 120 s (its player away from the prompt) is let go, freeing the seat.
- A melee that ends during a download refuses the client at its ready ("the melee is over"), as does one that ended
  before.
- A client leaving or lost mid-download frees its seat; the server logs how far it got.
- The listen server's own player has the map (Installed) and is seated after one round trip.
- The dedicated server shares from its own install (addon\, its folders, miss16\).

### Testing over a bad link

`I76_NET_LAG=ms[,loss%[,jitter]]` puts a local `LagProxy` between the client and the
server: that many ms each way (± the jitter, 10% by default) and that share of datagrams lost each
way. For example, `I76_NET_LAG=100,2` gives about 200 ms of round trip with
2% loss, `I76_NET_LAG=75,5,30` 150 ms ± 30 each way with 5% loss.

LiteNetLib's own `SimulateLatency` and `SimulatePacketLoss` do nothing in
its NuGet build: they are compiled only with DEBUG or SIMULATE_NETWORK.
That is why the tests use the proxy too.

## The wire

The transport is UDP through LiteNetLib 2.1.4 (MIT), in `src/I76.Net`
(no Godot). Every message starts with its type byte. Encodings are
compact:
- LEB128 varints;
- half floats for velocities;
- a 6-byte smallest-three quaternion for rotations;
- octahedral 4-byte directions.

| Message | Way | Delivery | What |
|---|---|---|---|
| join request | client → server | the connection request's data | magic, protocol version, name, car .vcf, team, a configuration blob (the car's .vcf when the server's install may lack it), the game's password (protocol 12, at most 32 characters, empty for none) |
| join accept | server → client | reliable ordered | slot, car id, arena, server tick, the car actually built, rules (teams, score and time limits, respawn delay), every car in the arena, the destroyed state of every structure that has lost anything, the melee kind, goal, goal value and team count, the rate, the arena's mission file and terrain sizes and CRCs (protocol 8), a custom map's hash (protocol 11), a co-op mission's part (protocol 15, `CoopInfo`: a flag, then the seats' cars, the respawn delay, this player's seat, each objective's flags, the cars with a siren on, the radio's line with how far into it, the end, protocol 19: the movie being played) |
| map offer, request, chunk, ready | both | reliable ordered (chunks on channel 1) | a custom arena's files before the seat (above, "Custom maps") |
| input | client → server | unreliable | the client tick and its last six `CarInput`s, newest first, then the tick of the snapshot it decoded last (protocol 13: the ack the delta snapshots are written against; the server keeps the newest it can use), then the hashes of the predicted car after the latest client ticks (protocol 13: a count byte with bit 7 "send the motion itself", the newest hashed tick as the gap back from the client tick, up to eight u32s newest first; below, "The cheaper own motion"); 71 bytes without hashes, about 100 with |
| snapshot | server → client | unreliable | server tick, the last client tick applied (the ack), the slack (protocol 13: the client's inputs left waiting after the tick took one, −1 when it found none), the baseline (protocol 13: how many ticks back the snapshot it is written against is, 0 for a full one), the recipient's own car, its motion at full precision (protocol 13, `OwnMotion`) or two bytes confirming the client's own prediction of it (header bit 4), the motion's slow part when it changed and its weapons' fire control (protocol 13, `OwnFire`), as byte deltas; then the cars and the rounds in flight that the snapshot's budget carries, each an entry of what changed since the baseline; then the CTF flags and the flame streams (protocol 13), then a byte of what follows of a mission's script (protocol 15: 1 the gates off their places, each structure, node and how far along; 2 the script camera while it holds the view, its eye and what it looks at or its basis; protocol 19: 4 the seats' personal cameras, each a seat byte and a camera; a melee's is 0) (below, "Delta snapshots", "The snapshot budget") |
| snapshot piece | server → client | unreliable | a snapshot too big for one datagram, in pieces (LiteNetLib fragments only reliable messages); one lost piece loses that snapshot. Since the snapshot budget only when a snapshot's essential entries alone do not fit (a full snapshot's head, a burst of removals): none in the load tests up to 64 players |
| events | server → client | reliable ordered | one tick's events, stamped with the tick: to each client those it is sent (below, "Events at scale"), effect and sound names by number once sent (protocol 13, `NameTable`: 0 and the string the first time, both ends adding it, then its number + 1) |
| scoreboard | server → client | reliable, once a second | each player's name, team, score, kills, deaths, ping and whether they are alive; protocol 13: a delta against the board the client has (a flags byte, 1 in full; each row that changed as its slot, a field mask and those fields, a ping only when it moved 10 ms; then the slots that left) |
| next round | server → client | reliable ordered | a new round on this connection (protocol 12): the arena; a map offer or the join accept follows (above, "Rounds") |
| chat, leave | client → server | reliable | |
| host candidate, successor | both | reliable | host migration (protocol 13, above "Host migration"): whether a client can take over and its addresses at home; who takes over, where, and the seat's token |
| migration state, migration log | host → successor | reliable ordered, channel 1 | the savestate in pieces and each tick's inputs (above, "Host migration") |
| car query | client → server | reliable ordered | protocol 13: the serials of cars the snapshots name and the mirror lacks (at most 32); one query a seat each 0.25 s is answered |
| cars | server → client | reliable ordered | protocol 13: the `CarInfo` of each car asked about that is still in the arena, on the events' channel |
| reconfigure | client → server | reliable | protocol 13: while dead, the car the next respawn builds (its file and, for the player's own variant, its .vcf; protocol 17: and its paint); one a half second is answered (above, "Reconfigure") |
| reconfigure answer | server → client | reliable ordered | protocol 13: taken (the car file it will build) or refused (why) |
| movie done | client → server | reliable | protocol 19: this client's copy of the co-op mission's movie is over (its name); the host's ends it for everyone (above, "Co-op over the network") |
| lobby state, chat | server → client | reliable | protocol 20, before the seat: a co-op game's lobby (above, "The co-op lobby") and its chat lines |
| lobby set, start | client → server | reliable | protocol 20: the ready mark and a new car (answered by `reconfigure answer`); the host's start (forced or not) |
| voice, voice from | client → server → clients | unreliable | protocol 20: the CB radio (below, "CB voice"): one 20 ms Opus frame of a player's voice with a sequence number and a start/end mark; the server relays it undecoded, with the sender's slot, to the seats that may hear it |
| join wait | server → client | reliable | protocol 13, before the seat: an invite-only game is asking its host (above, "The original's rules") |

- **A car in a snapshot** (78 bytes in full; an entry of what changed since the baseline, about 15–30 bytes for a car
  driving, 2 for one standing still):
  - id serial, generation, flags;
  - position (1 mm), rotation (the 6-byte smallest three), velocity (1/256 m/s), angular velocity (1/1024 rad/s);
  - steer, throttle, rpm, speed (1/128 m/s), gear, handbrake, wreck;
  - four suspension travels, two front steer angles (1/2048 m and rad), blown tyres;
  - the ten zone pools: armour and chassis for the front, left, right and
    rear, and the top's two mirrors. The body's looks, the lamps and the
    target box's health bar read them. A side's pool and its mirror only
    change together (`VehicleDamage.ArmourStep`), so one number writes both;
  - engine, suspension and brake health, a byte each (HP/max × 255; 0 only
    when gone, 255 only when whole). With the zones, that is all the target
    box's bar reads (`VehicleDamage.BarRatio`);
  - turret aim.
- **The recipient's own car** also carries the radar target, the
  speedometer, the selection and link group, each weapon's ammo and health,
  every damage part's exact HP (the damage panel), the specials' charges (bit 7: the jammer is on) and
  the driver's arms. Its armour and chassis come with its car above.
- **The recipient's own motion** (protocol 13, `OwnMotion`, 311 bytes with
  four wheels) is what the vehicle step and the controls carry from one tick
  to the next, at full precision (floats, the position and the suspension
  filter's state as doubles), for the prediction: the rows, position,
  velocity, angular velocity, acceleration, speed, steer, throttle, gear,
  handbrake, the exhaust brake, the ignition key, the drive selector, the
  flags, surface, ground normal, load and heights, the shared timer, the
  generation, the contact record (its other party as a car's or a
  structure's id), the engine's gear, rpm, speedometer and force, each
  wheel's travel, ground, filter state, limit and blow-out, and the driver's
  four key ramps (and whether they belong to an older car). Its slow part
  (109 bytes: the car's parameters, the engine's curve, peak and damage
  factor, each wheel's grip, rolling resistance and rest height: what only
  damage, a blow-out or a renewal changes) rides along for half a second
  after it changes and once a second; the client keeps the last it got. The
  wheels' spin and steer angle are left out: one is only drawn, the other is
  set from the steer at the start of every step.
- **The recipient's own fire control** (protocol 13, `OwnFire`, header bit 4),
  per weapon (the armoury's and the handguns): its index, a flags byte, its shot
  count (u16, the server's count of its shot events), then only what is not at
  rest: the two timers and the burst, the turret aim, the gun's pitch and the
  lock time. A weapon at rest is four bytes.
- **A round** carries its id, its shooter and weapon (so the client knows
  its look), position (1/128 m), rotation, distance travelled (1/4 m) and whether it has
  settled (a mine or Blox at rest, the chemical mortar's cloud).
- **A flame stream** (protocol 13, after the flags): the owner's id, the weapon
  and its number of cross-sections (2..19), 3 to 5 bytes.
- **Events:**
  - shots, impacts, explosions, clicks;
  - zone damage, deaths, wrecks, blown tyres, flung parts, debris;
  - knocked signs; structures losing nodes or their root (the whole
    destroyed state: bit 0 the root, bit n + 1 node n, and the explosions
    that went with it for their sounds);
  - cars spawned and removed;
  - players joining and leaving, scores, kills (with points), renewals
    (respawn or regen pad), chat, game over (with the pause before the next round);
  - protocol 13: a car rebuilt as another (`CarChangedEvent`, a reconfigured respawn), the end decided (`EndEvent`:
    the winner's slot, team and count, or the time limit; 10 s before the game over);
  - the driver's misfires;
  - collision and landing sounds (picked as SimDriver picks them);
  - protocol 15, a co-op mission's script (above, "Co-op over the network"): a radio line (sound by name number, the
    speaker's entity index or −2 the player or −1 none, the priority) and a cut, an objective's flags, a movie, the
    mission's end (won, the failure's number and line), a siren on or off;
  - protocol 19: the movie is over for everyone (`MovieEndEvent`).

### The simulation rate

The server's rate applies to everyone (docs/frame-rate.md §7.2). The host chooses it (`ServerOptions.Rate`; the
listen server's is the host form's choice, the dedicated one's `--rate 20|24|25|28|30|40|60`, default 20), and
`JoinAccept` carries it as `RateHz` (protocol 7). The client builds its session, input history and `ClientWorld`
from it and refuses a rate it does not offer; its own setting is ignored while connected (the session's rate is built into the client's session; Play Options' `[port] sim_rate`, which its
single-player games use, is neither read nor written by a network game).

**The host form.** GAME OPTIONS' SIM RATE row on the Nitro form and the co-op form, the Sim Rate row of the base
game's host form (a click steps it); kept in melee.cfg (`host_rate`) apart from Play Options' rate, the first time starting at
that one. `--host-listen --rate HZ` sets it from the command line. The session list's answer carries it (`SessionInfo.RateHz`, a
byte appended to its tail: 0 from a build that does not say), and the roster shows it beside the selected game and in its popup
(the Nitro popup at its foot, the base game's in the rules line). The join accept already carried it: no protocol change beyond
that appended byte, which an older reader skips (protocol 15 was never released; 0.8.0 ships 16).

**Physics is the original's for everyone, and not an option.** Modern Physics is a Jolt rigid body in the game process
(docs/modern-physics.md); the server is authoritative and runs the original's vehicle model (`SessionOptions.Network` has
`ModernPhysics = false`), and a client's predicted car must step the same model to agree with it. A client's Modern Physics
setting (and F7) therefore does nothing in a network game and every player drives the same model; GAME OPTIONS shows ORIGINAL
PHYSICS as a ticked box that cannot be changed. Modern Physics online would need the server to simulate that model too. A protocol-6 client is
turned away at the join. The wire's tick-count limits are written in seconds and become ticks of the server's
rate (`Protocol.InputHistory(rate)` 0.2 s of inputs repeated, `MaxQueuedInputs` 0.288 s, `CatchUpInputs` 0.2 s
(0.108 s before protocol 13), `MeleeClient` keeps 2.3 s of snapshots, `ClientWorld.StaleTicks` 0.36 s): at 28 Hz
these are 6, 8, 6 (was 3), 64 and 10. Snapshots go one a tick, so bandwidth follows the rate (the figures below are at the default 20 Hz; 60 Hz sends three times as many).

### The server

`src/I76.Server`: `MeleeHost` is the game without sockets, and
`NetMeleeServer` puts it on LiteNetLib and runs it at the fixed tick of its `ServerOptions.Rate`.

- **The game mode** (`HostMode.cs`). The host is the wire's side of a game (seats and their inputs, snapshots and their
  budgets, the event stream, chat, maps, migration's transport); what the game *is* sits behind `IHostMode`, which
  `MeleeHost(options, HostModeFactory?)` builds from a `ModeContext` (options, install, the loaded arena) and then asks for:
  `Setup` and `Build` (the session's world and what it is rebuilt from), `Start`/`Opened`, `Over`/`Ended`/`Concluded` (the
  game-over event), `Rules` (the join accept's and session list's mode fields), the seats (`SeatsChanging`, `Admit` a joiner's
  car, `Release`, `RaiseDead`, `CanReconfigure`/`Reconfigure`, the server-driven `Bots`/`BootBot`), the tick's mode events
  (`Collect`: kills, scores, respawns, the end), the `Board` rows, the CTF `Flags`, and `Restore` (a successor's seats from its
  restored session's drivers). A seat holds an `ISeatActor` (car, file, team, numbers; `Seat.Player` is the melee's
  `MeleePlayer` behind it). `MeleeMode` is today's game, everything above this paragraph: melee, race and CTF, the AI fill,
  bots. The mode reaches the host through `IModeHost` (events, log, the seats, the spawn bookkeeping). A client's mirror has the
  matching seam in `ClientWorld`: `IMirrorCars` builds and rebuilds its cars (`MeleeMirrorCars` for a melee).

- **Cars.** A join's car is built from the server's install by its name. The
  name must be a plain `name.vcf` that the install has; anything else gets
  the default car. Nothing the client says about the car's numbers is used.
- **Inputs** (`InputQueue`):
  - Each client tick is applied once, in order, one per server tick. The
    repeats in later messages cost only bytes.
  - With nothing waiting, the last input's held keys and analog position
    are held, without its one-off commands.
  - Unknown bits are dropped. Skip and poem (single player's) are dropped.
    Analog values are kept in ±32767 and zeroed without the analog bit.
  - One command of each kind per tick holds by construction: the commands
    are flags.
  - More than eight waiting (0.288 s): the oldest are dropped. More than
    0.2 s of them waiting when a tick takes one (a burst after a stall; 0.108 s
    before protocol 13): they are merged down. In both cases their commands
    are carried forward.
  - Each tick's `Slack` (the inputs still waiting after it took one, −1 when
    it found none and held the last) goes into that client's snapshot.
  - Malformed messages are logged and ignored.
- **Players.**
  - Leaving frees the slot (`SimSession.RemoveDriver`) and takes the car out
    of the world (`MeleeGame.RemovePlayer`).
  - Respawns, regen pads, scores and the end are `MeleeGame`'s rules (above).
  - The tick's `Combat` and `DamageModel` event lists are turned into events
    and cleared every tick.
- **Faults.** A tick that throws stops the loop, records the fault, and the
  console server exits.

### The client

`MeleeClient` (no Godot) connects, sends the sampled input every tick and
keeps the last 64 snapshots and the events.

**The view** (`ClientWorld`):
- It is a mirror `MissionSetup`, the arena set up as a network melee with no
  cars, that is never ticked (only the own car is stepped, by its
  prediction). Its cars are built from their .vcf as the
  server announces them and written from each snapshot.
- So the game's existing views draw them unchanged: CarNode, CombatView,
  DamageView, the cockpit, the HUD and the target indicator.
- **Every car the server has is built** (protocol 13):
  - A car the snapshots name that the mirror lacks for 0.5 s (its spawn event lost, or thrown away by a view) is asked
    about (`MeleeClient.AskCars`, again each second while it is missing); the server answers with its `CarInfo`, and the
    mirror builds it when the view next moves to a new snapshot. A car removed is never brought back by a late answer
    (serials are not reused within a round), and one that leaves the snapshots is no longer asked about. The delta
    snapshots' baselines are the connection's (`SnapshotDecoder`), not the mirror's, so a car the mirror lacks still
    decodes: each snapshot lists every car the client's frame holds (a far one with its older state), and that is what
    is checked; the car built late takes its states from the snapshots still held.
  - A car this install cannot build (another game's model, a part it lacks) is built as the same model's first variant,
    else the default car (`ClientWorld.StandInCar`), and logged: a player drawn as another car can still be found.
    `MirrorCar.StandInFor` names the car it stands in for. The game does the same when a car's node cannot be drawn.
  - Each event is applied on its own: a view that throws on one (the game's car node, a sound) is logged
    (`ClientWorld.Log`) and the rest of the batch, and the batches after it, still apply. One throw used to lose every
    event released with it, the backlog of a join included.
  - The mirror reads the connection only while the client's accept is the one it was built from: a new round's events
    belong to the next scene's mirror. A round that begins while a joiner's arena is loading is carried into at once.
- This is simpler than a separate render-only model, and it is what the
  prediction steps the own car in (the same terrain and structures as the
  server's).

**Timing:**
- The view runs 100 ms behind the newest snapshot. Its clock is gently
  pulled towards the newest snapshot's tick less that delay, and jumps when
  more than 10 ticks off.
- Each other car is drawn on a clock of its own (`CarTrack`; protocol 13's interest management sends far cars less
  often): the view's, less the car's send interval less one tick, so there is a later state of it to interpolate to (0
  for a near car, 50 ms for one sent every 0.1 s, 150 ms every 0.2 s at 20 Hz). The extra delay changes by at most 10%
  of the time passing, so a car changing rate speeds up or slows down a little instead of jumping.
- A car near the own predicted car is drawn at the predicted present instead (below, "The cars near the own car").
- A car with no later state (snapshots lost) goes on along its velocity for up to 0.25 s, then holds. When the next
  state moves the curve under where the car was drawn, the difference is drawn away over about 0.1 s (`CarNode.LivePose`
  is the drawn pose); a respawn, or more than 8 m, is drawn at once. The wheels and roll follow the car's own pair of
  states (`MirrorCar.PoseVersion`). The mirror car itself holds its latest state the view has reached (the radar,
  the target box and the sounds read it).
- The own car is not written from the snapshots the view reaches while it is
  predicted (`ClientWorld.Predicted`): the prediction writes it from the
  newest. The prediction's stand-ins for the other cars are moved on from each car's own tick.
- Events are released when the view reaches their tick:
  - shots, impacts and explosions go into the mirror's `Combat.Events`;
  - damage goes into its `DamageModel.Events`;
  - the rest goes to the network HUD.
- Shots and sounds from more than 10 ticks back (what came while the arena
  loaded) are skipped. Their lasting results are still applied.

**In the game:**
- `SimDriver.Remote` sends the input each tick (on the input clock, below) and
  does not tick locally; the own car's prediction steps with each input.
- `NetSession` polls the connection even while the tree is paused.
- `NetHud` shows ping, the goal line, the console's lines (kills, joins, leaves, chat, in the original's words), the
  score window while Tab (or a pad's Back) is held, the listen host's prompts (an invite-only join, a boot), the dead player's way
  back ("Space: come back" once the 5 s are up, "Enter: reconfigure"), each machine's end line and the final standings.
  It is a placeholder for the original's screens (Net-5). For many players: the score window is a page of `BoardRows`
  (10; the host's own H row and nine numbered) with the own row kept in view and "1-10 of 61" under it; Ctrl+PgUp/PgDn turn the page. The feed shows at most `FeedBurst` (4) joins, leaves and kills
  per `FeedWindow` (2 s) and counts the rest into one "... and N more" line; chat and the console's lines are never held back.
- The player's own car is predicted (below): drawn at the present, not
  100 ms behind. `I76_NET_PREDICT=0` turns the prediction off (the own car is
  then drawn behind with the others), for A/B and debugging.
- **Chat:** the original's CHAT_SENDTOALL game key (0x27) opens a one-line
  entry ("say: ...", bottom left) in `NetHud`. Its default is Backspace, one of the
  hard-wired defaults of the key-map loader; a `CHAT_SENDTOALL` line in
  `gamekey.map` rebinds it, as does the Control Configuration's Chat row. Enter sends (an empty line is not sent), Esc cancels.
  While it is open `NetHud._Input` takes every key press, so no game key (views,
  Esc menu, Ctrl+F10) acts, and `Controls.Suspended` makes every command read as
  not held and the sampled `CarInput` is `WhileTyping()` (driving, fire and commands
  cleared: the car coasts and the game does not pause). Characters come from
  `TextEntry.Typed`, the layout's own, as in the shell's fields. It works in a
  client's game and in a listen host's own game (a network game only); no wire
  change (`MsgType.Chat`). The server clips a line to `MaxChat` and limits each
  player to 5 lines per 5 s (`ChatLimiter`, server time), dropping the excess
  and logging the first drop of a burst; every line is logged on the console as
  `chat: NAME: TEXT`.
  **Reading it.** Chat lines stay `HudFeed.ChatLife` (30 s, the other lines' ten) and the last `KeepChat` (40) are kept as a
  history: in the 1997 look the console window (the original's eight lines at (50, 150), red Lee) shows the chat for that long
  among the other lines, and while the Say line is open only the chat, as far back as fits; the modern look's chat panel
  likewise (the last eight lines, and as many of the last twenty as fit while typing). Long lines wrap in both looks (a word
  wider than the window is broken), the Say line scrolls to its end, and the modern panels (feed, chat, prompts, the standings'
  columns, the end banner) keep inside the safe frame at any window size or aspect. A new player is reminded of the chat key and
  of Self Destruct (a new car) in the modern look's top strip and the 1997 look's top-right lines, until used or 90 s.

- **Damage rules.** A network melee's cars, the players' and the bots', follow the original's rules for a network session: no player rescale (armour and chassis x2, parts x4 are single player's), the kill roll's 2.5
  (single player's player has 0.1) and the engine's 0.4 floor. Before, every network car had the single-player player's
  toughness, and a healthy car took about 150 .45 shots to kill instead of about 68 (#109).
- **Helicopters** (the hidden Huey and Cobra, the Nitro balloon, UFO and Phantom: object class 9). The melee's
  car builder (`MissionSetup`'s `Build`, used for joins, respawns and the client's mirror) gives a class-9 car
  `HeliStep.Setup`, as the mission loader does: the helicopter block, the pool from the front armour, flags
  engine-on and landed, no car damage state. Before, the server stepped it as a car, and it spun and
  exploded. The pool rides in the car's first zone (`CarState.Zones[0]`); the flight state (the eased
  throttle and side input, the height held, a contact this frame) rides in the own motion (bit 64 of its
  flags byte, 13 bytes), so the prediction is exact. `PredictDamage` takes a predicted kill or crash (the state, not the
  explosions). A respawn builds a fresh block. The S key does nothing to a helicopter, as offline.
- **Handguns.** The driver's .45s are fitted by the car builder for every human's car
  (`VehicleFactory.AddDriverHandguns`): the car's interior model (detail 5) with a GUNR part has the right arm,
  GUNL the left, the rule the game uses offline at the cockpit. The firing rule is the original's and
  was already in the tick: with the cockpit bit and an arm raised (the glance to a window, sent as
  `RaiseRightArm` / `RaiseLeftArm`) the fire key works that arm's gun and not the hardpoints; with the arms down, or out of
  the cockpit, the hardpoints answer. The bits are input, not rendering, so gameplay depends on what the player
  sends, as for the cockpit bit already.
- **Names** (Ctrl+T, `TOGGLE_ALL_NAMES` 0x28, `NetNames`). The original's flips and draws names from it: off, only the radar target's name (a player's car); on, every player's car
  that is alive with its engine on. A name is the text alone, centred above the car in the team's colour
  when in front of the camera and on screen; there is no damage readout (the target box has the bar). Network games
  only; the toggle starts off and is not saved. The port's name sits 0.6 m over the roof, in range 800 m (the original's anchor
  and range are its own table and far-plane values; ours are approximations), and skips your own car. With many players the
  port draws the nearest `MaxNames` (12) only, skips a name that would sit on a nearer one, and fades the far ones (full within 150 m, a third at
  800 m); the original's table had room for eight.
- **Engine loops.** The server does not send the engine events; the client's engine loops follow each car's
  EngineOn flag (`NetView.Advance`), and the own car's start and stop also come from the prediction.

### Prediction (protocol 13)

`OwnCarPredictor` (`src/I76.Net/Prediction.cs`, no Godot) predicts the
client's own car; the game's `NetView` drives it.

- **Stepping.** Each input the client sends (client tick N) is stepped at
  once on the client's own copy of its car (the mirror's), by
  `SimSession.PredictTick`: the fresh-car check, the driving commands, the
  controls, then `SimWorld.FrameOne`, the world's frame for that car alone
  (its scenery contacts, its pairs with the other cars, its step). It is the
  same code the server's tick runs for that car, on the same frame delta:
  input N runs on the server tick whose count is N + (S − A) − 1, where the
  newest snapshot S acked input A. That matters at 20 Hz, where two frames in
  five are 103/2048 s and step the car twice.
- **What is predicted:** the driving (throttle and steer ramps, handbrake,
  the stick), the engine key (ignition, the engine starting and stopping:
  the damage model's timer runs), lights, the drive selector and reverse, the
  specials (nitro, the jammer, the exhaust brake; on a fresh tick nitro's and the jammer's sounds, bnitro.wav and
  cjamm.wav, play from the predicted car at once and the server's click for them is not played: 0.5.0 left them to the
  server's click, a round trip and the view's 100 ms late, and testers heard "a delay on the nitrous") and the weapon selection and link. The dash, the
  speedometer, the PRNDL strip, the engine's sound and the force feedback read
  the predicted car. On a fresh tick (not a replay) the ignition and the
  engine's start and stop go to the damage view, so they are heard at once
  (a network client heard none before).
- **What is not:** damage (`PredictDamage` stands in for the damage
  model inside the step: no impact, landing, rough-ground or ram damage, no
  fall kill, no signs knocked), impacts, the radar and targets, the poem,
  self-destruct. A dead car waiting for RAISE_DEAD is predicted exactly: its death timer runs as the server's, only the
  death's effects (the parts flung, the smoke) are left to the server (in a load test of eight bots, 2 inexact ticks in
  1608 where main had 102). They stay the server's, as before. The own shots are fired at
  once from the predicted car, but only for drawing (below, "Own shots at once").
- **Reconciliation.** When a snapshot newer than the last one taken arrives,
  the predictor puts the car back as the server had it after the acked input
  A: the own car's `CarState` and `OwnCar` (looks, armoury, parts) and then the
  `OwnMotion` over them; then it steps inputs A+1..N again from its ring (256
  ticks), and compares what it had predicted for A with the server's (the
  `OwnMotion` encoding, exact; and the distance). A tick whose input never
  reached the server in time (it held the last one) or that the server merged
  away shows here as a miss, and the replay from the server's state puts it
  right.
- **The other cars** are not predicted. The pair test uses them as the newest
  snapshot has them, moved on along their velocity to each replayed tick
  (`FieldCopy` stand-ins: the mirror's own copies are left as drawn). A crash
  into another car is mispredicted until the snapshot of it comes, and put
  right then. So is a structure the server destroys under the car (the client
  only learns of it when the view reaches the event).
- **Drawing.** The car is drawn between the last two predicted ticks at the
  input clock's fraction (`CarNode.LivePose`; the wheels' steer, travel and
  roll by `SetPoses` each predicted tick). A correction moves the predicted
  present; its jump is kept as an offset (position, and rotation about the
  car) that decays with a 0.1 s time constant, so it is drawn away while the
  car itself has snapped. A respawn or regen pad (the generation changes), or
  a jump of more than 8 m or 45°, is drawn at once. The camera, cockpit and HUD
  follow the drawn car.
- **The input clock.** The client's ticks are nudged so that the server keeps
  one of its inputs in hand (`MeleeClient.ClockScale`): with the fewest
  waiting over the last second (the snapshots' slack) under one, the clock runs
  2% fast per missing input (up to 4%); over one, 1% slow per extra input (up
  to 3%). The server merges a backlog only past 0.2 s, so a jittery link's
  bunches fit above that margin. Without this, a jittery link starved the
  server or had inputs merged away, and each was a miss of a tick's travel
  (a metre or more at speed).
- **Debugging.** `I76_NET_DEBUG=1` puts the last error, correction and
  offset under the ping and logs each correction over 1 cm with a summary
  every 30 s; `=2` also logs every input tick (the predicted car against the
  newest the server sent).
- **What it costs.** 311 bytes a snapshot in full (420 with the slow part); as a byte delta against the acked
  snapshot (exact) about 190 bytes for a car driving, the slow part only when it changed; and since the motion is
  confirmed by the client's hash (below, "The cheaper own motion") two bytes in nine snapshots in ten; a replay of
  RTT + slack ticks (5 or 6 at 150 ms) of one car each snapshot.
- **Known effects.** A client
  that stalls (a hitch longer than the input or two in hand) starves the
  server, which holds the last input; the next snapshot corrects it.

Measured (the tests below, m01, no other car, 20 Hz): at 100 ms round trip,
227 of 227 compared ticks exact; at 150 ms with ±30 ms jitter and 5% loss,
205 to 211 of as many exact. The only drawn corrections were in the first
second, while the input clock settles (4 mm to 0.4 m), and the test's 10 m
teleport (snapped); after it every compared tick was exact again. End to end (an i26-server and a client through
`tools/shot.sh` with `I76_NET_LAG=75,5,30`): the predicted car's speed rose
on the first input tick with the throttle, the server's 8 ticks later; no
correction in 130 ticks. A listen host with two AI cars: none in 210 ticks
but one at the capture's last frames, where saving the picture stalls the
game.

### The cars near the own car

Testers of 0.5.0: "when you bump into each other online it's like there's a 5 inch force field around the cars; you don't
see the bump but you feel it". The prediction meets the other cars where they are now (the newest snapshot moved on along
the velocity, above), but they were drawn on their own clocks, 100 ms and a round trip behind: at the moment the own car
was pushed, the other was drawn its speed times that delay short of it. Measured (`ContactTests`, m01, 20 Hz, Piranhas,
snapshots 3 ticks late): b at 8 m/s into a parked a, 1.81 m apart on a's screen when a was pushed; both at 8 m/s, 2.12 m;
both at 15 m/s, 4 ticks late, 4.32 m.

Now `ClientWorld.StepCars` gives each car's `CarTrack` the predicted present (`OwnCarPredictor.PresentTick` at the input
clock's fraction, and where the own car is drawn). A car within 15 m of the own car (centre to centre, at the present) is
drawn at the present: its newest state moved on along its velocity, as the prediction meets it (at most 0.5 s past that
state); beyond 40 m on its own clock; between, a smoothstep blend. A new state's jump is drawn away as before (0.1 s). The
same cases measure 0.19, 0.53 and 0.66 m: the server's own gap when it meets them, which is the original's (its box test
looks a frame ahead and the response does not move the car to the contact: at 20 Hz up to the closing speed × 0.05 s, as
in single player and in the original at 20 fps). The boxes are the bodies within a few centimetres (`CarBoxTests`).
A near car turning round a parked one is drawn 0.48 m from where the server has it at the present on average (1.53 m on
its own clock), and as smoothly (the 99th percentile of the frame-to-frame change of its drawn velocity 5.2 m/s against
4.8). Where the car does what its velocity does not say (a crash into a wall, a bump from a third car) it is drawn on for
a round trip and then drawn back. `I76_NET_NEAR=0` draws every car on its own clock.

### Delta snapshots and interest management (protocol 13)

Every client used to get every car and round in full every tick: the host's upload grew with the square of the
players, and from 16 players every snapshot went in pieces (one lost piece losing it). Now (`src/I76.Net/SnapshotDelta.cs`,
`Interest.cs`, `CarTrack.cs`; protocol 13 was unreleased, so it was extended rather than bumped):

- **Baselines.** Each input message carries the tick of the snapshot the client decoded last, and the server takes the newest of these it did send. The server keeps, for each
  client, the frames it sent it in the last 1.6 s (`SnapshotEncoder`: the own car's bytes, its motion's, and each car's
  and round's channels as that client has them) and writes the next snapshot against the frame of the acked tick; the
  client keeps 3 s of the frames it decoded (`SnapshotDecoder`). With no acked frame (a new joiner, a mid-game join, a
  new round, acks that stopped for 1.6 s) the snapshot goes in full. A lost snapshot costs nothing more: the next is
  against an older acked one. A snapshot naming a baseline the client no longer has is dropped, never misread.
- **Cars.** A car is 23 integer channels in six groups (position, rotation, velocity, angular velocity, the driving
  values, the wheels; the precision is in "A car in a snapshot" above) and its cold bytes (generation, flags, gears,
  tyres, the damage zones and parts, the turrets). An entry is the serial (the gap from the last), a control varint (bit
  0: the car is gone; then a bit per group that changed) and the changes as zigzag varints; the position as the
  difference from where the baseline's state, moved on by the mean of its old and new velocity, puts it; the cold bytes
  as a byte delta. A driving car is 15–30 bytes, a parked one 2.
- **Rounds** likewise (distance travelled, position, rotation, cold bytes: shooter, weapon, settled); the position from
  the distance travelled along the round's heading; a new round's baseline is its shooter's car in the same snapshot; a
  round unchanged since the baseline is not listed.
- **The own car** (`OwnCar`) and its motion (`OwnMotion`) go as byte deltas of their encodings (`XorDelta`: runs of
  the same bytes and of the XORs of the others), so the prediction's restore stays bit for bit. The motion's slow part
  goes only when it differs from the baseline's: the client then always has the server's (the old resending for half a
  second and once a second is gone, except in full snapshots).
- **Interest.** Each car is due every tick within 200 m of the client's car, within 600 m ahead of it (±60°), when it is
  the client's radar target, when it is after the client (its radar target, or an AI ordered to attack it), and the own
  car; every 0.1 s within 600 m (the radar's long range, where the original drew its last car); every 0.2 s beyond.
  Rounds within 200 m every tick, within 400 m every 0.1 s, then every 0.2 s up to 650 m, and not at all beyond. What
  is due goes as the snapshot's budget allows (below, "The snapshot budget"). Only what each client is sent changes: the game is the server's, and every car stays in
  every mirror (radar, target box), only with an older state while far. A car's entry says it is of this tick; a car not
  listed keeps the state it had, and the views draw each car on its own clock (above, "The client").
- `ServerOptions.DeltaSnapshots` and `InterestManagement` (both on) turn them off for A/B tests.

### Many players: the snapshot budget (protocol 13)

Delta snapshots and interest management held 16 players; at 32 a tight fight sent half the snapshots in pieces, at 64
almost all, and 24 Mbit/s of upload. What takes it to 64 (protocol 13 was still unreleased, so it was extended again):

- **The snapshot budget** (`SnapshotPlanner`, `src/I76.Net/Budget.cs`). Each client's snapshot fits one datagram: the
  path's MTU less LiteNetLib's header (`GetMaxSinglePacketSize`, about 1400 bytes; 500 until MTU discovery has run), and
  with an upload limit (`ServerOptions.UploadBytesPerSecond`, `i26-server --upload MBIT`, `--host-listen --upload MBIT`)
  the client's share of it: the limit over the players, less the bytes a second its events have taken lately (a 2 s
  average) and 768 bytes a second of overhead (`Protocol.ClientOverheadBytesPerSecond`), never under 4 KiB/s.
  - Every car and round has a priority accumulator per client, grown each tick and reset to nothing when it is sent:
    a car by its interest rate (1 a tick when due every tick, ½ every 0.1 s, ¼ every 0.2 s at 20 Hz), plus how far the
    client's copy (the baseline's state moved on along its velocity for at most 0.25 s, as the client draws it) has
    drifted, seen from the client's car (30 × metres / max(distance, 10 m), at most 2), plus 1 when its looks (the cold
    bytes) changed; a round by its distance's rate. A new one starts at a fraction set by its serial (the golden ratio),
    so those of one rate fall due on different ticks. An entry is due at 1.
  - The snapshot carries the head (own car, motion or its confirmation, fire control) and the tail (flags, flames)
    always; then the essential entries: the own car, removals, a respawned car (its generation changed), anything not
    sent for two seconds; then whatever has waited a second, oldest first; then the due entries by priority (the
    accumulator times 2 / (1 + distance / 150 m) for a car, 1 / (1 + distance / 150 m) for a round, one more for a new
    round, its tracer starting at the gun; a car the client does not have yet first), as long as they fit. The rest
    wait, their accumulators growing, and rank higher next tick. Each entry is encoded once against the client's
    baseline (`SnapshotCodec.Plan`), the choice made (`SnapshotPlanner.Select`, the serial gaps counted exactly), then
    the chosen written in serial order (`Emit`); the client keeps the baseline's state of the others, as for a car
    interest management skipped. A snapshot goes over its budget only when its essential entries alone do not fit, and
    then in pieces.
  - A new round's baseline is now its shooter's car in the baseline frame, moved on along its velocity, rather than in
    this snapshot: a round's entry no longer depends on which cars this snapshot carries.
- **Coarse far cars** (`CarSend.Coarse`). A car beyond 200 m that is not related to the client goes without its
  driving values and wheels (its engine is not heard past 250 m, its suspension not seen) and at a coarser precision
  (`SnapshotCodec.CoarseUnits`: position 1/32 m, the rotation's smallest three in steps of 32, 0.16°, velocity 1/16 m/s,
  angular velocity 1/64 rad/s); its control varint has bit 0 set with its groups (alone, bit 0 is a removal). The frame
  holds what the client has, so a car coming near is corrected from its coarse state by its next full entry.
- **Rounds past sight** (`Interest.RoundCullMetres`, 650 m: past the radar's range, where the original drew nothing) are
  not sent; one the client had is removed, and comes again, new, when near. A round unchanged since the baseline (a mine
  at rest) still costs nothing.
- **Remote cars at uneven intervals.** A car the budget put off comes a tick or two late, so its interval is no longer
  the tier's: `CarTrack.Interval` is now the gap three in four of the last twelve gaps were within (a few lost snapshots
  do not lengthen it), and the car is drawn that much behind; the longest gaps go on along the velocity.
- **The cheaper own motion.** The client hashes its predicted car after each input tick (`OwnMotion.Hash`, FNV-1a over
  the motion's encoding) and its inputs carry the latest hashes (up to eight, the latest say for a tick winning; a replay
  can change one). When the server's car after the acked input hashes the same as the client said, the snapshot carries
  the hash's low 16 bits instead of the motion (header bit 4): the client restores its own prediction for that tick
  with those bits (`OwnCarPredictor` keeps up to four per tick), so the restore is still bit for bit. The frame keeps the
  baseline's motion bytes, so the next motion sent is a delta as before. Any difference (a crash into another car, a
  late or merged input, the server moving the car) and the motion goes whole; a confirmation naming a prediction the
  client no longer has asks for the motion itself (`NeedExact` in the inputs) until it comes. `ServerOptions.ConfirmMotion`
  turns it off.
- **Events at scale** (`EventInterest`). At 64 players the events were 12 KiB/s to each client, nearly all of them
  shots, impacts and explosions on the far side of the arena. Each client now gets its own batch: what changes its game
  (damage, deaths, wrecks, tyres, structures, cars and players coming and going, scores, kills, renewals, chat, the end)
  as before; shots, impacts, debris and flung parts within 600 m; collision, landing and click sounds within 400 m (a
  car's sounds are silent past 300 m); explosions within 2 km; a misfire to its driver only; the own car's always. The
  effect and sound names go once per connection and by number after (`NameTable`). 4–5 KiB/s at 64 players.
  `ServerOptions.EventInterest` turns it off. A deviation: the original's far impacts were heard, held at a quarter of
  their volume past 400 m (docs/deviations.md).
- **The scoreboard** goes as a delta against the client's (a few bytes a second instead of 1.5 KB at 64 players).
- **The server's CPU.** At 64 players the snapshots took 7 ms of the tick on one thread. Each client's snapshot is its
  own work (its baseline, priorities, plans, and the tick's capture read only), so they are written on all cores
  (`Parallel.For` from 8 clients) and sent in order after; i26-server runs the server GC, which keeps the threads from
  waiting on one heap: 1.1 ms for 64 snapshots (Release, a 12-core laptop). The combat frame's collider test, called for
  every round against every car, allocated three arrays each time; it no longer does. The car pairs (O(n²), a
  bounding test first) stay as they are: the world's whole frame, the pairs included, is 0.9 ms at 64 cars.

### Own shots at once (protocol 13)

Before this a client's own muzzle flashes, sounds, tracers and rounds were the server's events and rounds, drawn at the
view's 100 ms behind the newest snapshot: at speed they started metres behind the predicted car. Now each fresh input
tick (not a replay) also runs the own car's weapons, and only for drawing (`SimSession.PredictFire`,
`Combat.PredictFrame`, `OwnShots` in `src/I76.Net`):

- **Firing.** After the car's step (`PredictTick`), the tick's triggers (the arms' gate), its arcs and
  turret and gun aims, each weapon's fire-control timers and its flame streams, in the server's order
  and on the server's tick delta. A shot goes into the mirror's `Combat.Events` (the views play its flash and sound and
  the force feedback its kick, from the predicted car's muzzle) and its round into `Combat.PredictedRounds`, a list of
  its own that the views draw with the server's, interpolated on the input clock. Those rounds fly the server's code
  and stop at what they meet in the mirror, harming nothing: no damage, no push, no impact or explosion
  (`Combat._cosmetic`). The spread comes from the mirror's rand, so a gun's scatter is not the server's.
- **Replays.** When a snapshot puts the car back, the replayed ticks run the timers, the ammo and the aims only
  (`Combat._silent`): nothing is fired twice. The own weapons' state comes back with the car: ammo and hit points in
  the own-car block, and the fire control in a new block, `OwnFire` (each weapon's two timers, burst, lock, turret and
  gun aims, and its shot count).
- **The server's copies.** The server's shot events for the own car are not drawn (`ClientWorld.Apply`). Its own
  rounds of the fast kinds (guns, cannons, rockets, missiles, mortars and the chemical mortar in flight, Ball O'Flame)
  are hidden: the drawn ones are the predicted. The lasting kinds (oil 0xc, caltrops 0xe, mines 0xf, Blox 0x10, fire
  0x11, kind 0x13, and the chemical mortar's cloud once landed) are the server's to keep, since they lie where they
  are for everyone: the predicted copy is drawn until the server's appears in the view (landed, for the chemical
  mortar), then the oldest predicted copy of that weapon gives way to it. One the server's does not replace in time
  (round trip + view delay + 0.6 s, 3 s more for the chemical mortar) goes.
- **Rejected and missed shots.** The server counts each weapon's shot events (`MeleeHost.Shots`, wrapping at 2^16);
  `OwnFire` carries the count after the acked input. At each snapshot `OwnShots` compares it with the shots it drew up
  to the acked tick: fewer (out of ammo, the gun destroyed, the car dead, a trigger the server never had because the
  input came too late) and the newest predicted rounds of that weapon up to that tick are taken away; more (an input
  the server held, shots before the prediction began) and that many of the newest server rounds of that weapon are
  drawn. The counts start again from the server's at a new car or a renewal. The flashes and sounds of a refused shot
  have already played; nothing takes them back.
- **Impacts and damage stay the server's**, drawn when the view reaches them: a predicted round vanishes where it meets
  something, and the server's impact comes about a round trip and 100 ms later, at the server's point (which, with the
  car predicted exactly, is the same for a hit on scenery; a car is drawn 100 ms behind, so a hit on one can differ).
- **Missiles** home on the radar target as the client has it (the target is in the own-car block, the cars where the
  view draws them): the visual follows what the player sees; the server's missile decides.
- **Flames.** The own car's streams are its own copy in the mirror, grown by the predicted shots and built from the
  predicted car every fresh tick; the server's own-car entries in the snapshot's flames are skipped.

`I76_NET_PREDICT_FIRE=0` turns it off (the own shots are then drawn from the server's events and rounds again, its
flames from the snapshot); `I76_NET_DEBUG` adds the shots to its summary. Tested in `CombatEffectsTests` (below).

### Flames and lingering hazards

- **Flame throwers** were invisible on every client: their streams (`Combat.Flames`) are not rounds, so
  they were in no snapshot, and the mirror is never ticked, so nothing made them. The snapshot now ends with the
  streams (`FlameState`: the owner, the weapon, the number of cross-sections). The client builds each from its copy of
  the car as the view draws it (`Combat.BuildFlameView`: the same chain, droop, whip and flattening on what it meets,
  but no damage and no events: the burn and the xflht1 bursts are the server's events). The views draw them as in a
  local game, both looks (NapalmModern, FlameStreams).
- **The Chemical Mortar** (Nitro, `ggchem`, kind 0x16) went off like an explosive mortar on clients, and in single
  player too: kind 0x16 flew the generic path and was spent at its first hit. It is now the original's
  (the same code in the Nitro Pack; `Combat.StepChem`): it flies as the mortars do, its
  path tested against the cars only; a car met takes its packet (45, armour bypassed); then it lands on the ground
  under that point and lingers as a cloud (`Round.Settled`) for the rest of its 16.7 s life. Every tick, a 1 m probe up
  from it finds a car in it, which takes the packet and the `xclst1` impact with its 45-point splash over 20 m. On the
  server that is ordinary sim state, so the harm is authoritative; the round (with its settled flag) is in every
  snapshot, so every client draws the cloud (`ChemClouds`: the original's `xcp1` puffs in the 1997 look, a green
  billowing cloud in the modern look), and the impacts come as events.
  Testers of 0.5.0: "it doesn't kill anyone at first, then it does after you drive into it a few times". Checked: online
  the cloud kills the driver on the first pass, on the tick the car's box reaches the landing point (the server's car,
  which the victim's prediction agrees with exactly; `DrivingOverTheChemicalCloudKillsOnTheFirstPass`); nothing to
  compensate, the cloud does not move. What kills is the Nitro Pack's 1 m probe straight up from that point
  (the original's `+1.0` on the probe's end), not the drawn puffs: a car passing 2 m beside it is not
  touched (`PassingBesideTheChemicalCloudDoesNothing`). That is the original's, and it is kept. A driver killed at the
  wheel sounds the horn until the wreck timer's end; a respawn before then left it sounding for the rest of the game
  (`DamageView.Restore` did not stop it): now a respawn, a regen pad and a reconfigured car stop it.
- **The other lingering hazards** were already right on clients: oil, caltrops, mines, Blox and fire drops are rounds
  in the snapshot (drawn by OilSlicks, FireDrops and the round models), and what they do (oil's lost grip, a mine's
  throw, the fire's burn) is the server's, which reaches the client through its car's state and the events. Ball
  O'Flame (kind 0x15) is a bullet-like round that leaves nothing behind (the texture animation released
  at its end is not a fire), and 4Get-U-Not (0x14) a guided missile: neither lingers.
- **The caltrops** (Nitro, kind 0xe) only made their sound, in single player too: `CaltropTyreHit`
  was not modelled. Now a car over one has its nearest tyre set to 25 of 100 and blown (Tire Covers: a quarter off a
  frame instead; `DamageModel.CaltropTyreHit`). On the server that is ordinary sim state: the blown tyre
  reaches every client in the car's blown bits, its `tblow.wav` as the TyreBlown event, and the own car's tyre hit
  points in the own-car block; the caltrops are rounds in the snapshot as before.
- **The 4Get-U-Not** (Nitro, class 4 tier 4, kind 0x14) could never fire: its trigger waits for a lock (`Weapon.LockTime`
  ≥ 1.6 s) that nothing built. The lock is now the original's (`Combat.Lock`): it grows while the
  selected weapon's arc holds a target and the player's trigger reads it after the frame's aims. The server builds it
  for every human's car; the client's prediction builds its own (and `OwnFire` carries the lock, as before), which
  plays `msllock1.wav` / `msllock2.wav`. The car it holds hears `msllock3.wav`: the own-car block has a bit for it
  (`OwnCar.LockedOn`, in the flags byte with Link and the radar's; no protocol change, 13 is unreleased). The missile flies as the radar missile without its letting go: it follows its target through a jam and death,
  and keeps 0.5 m over the ground.

### Bandwidth

Measured with the load test (`LoadTests`, below): a server and N headless clients in one process on m01, half of
them hunting the nearest car and firing, half roaming within 400 m of their spawn point, spread over the arena's 17
spawn points (30 m apart along a point's side when there are more players than points), 20 s after a 3 s settle; the
links are clean, 50 ms each way (100 ms round trip), and 75 ± 30 ms each way with 5% of datagrams lost. Every client
runs a mirror and the own car's prediction (drawn at 60 frames a second), so each sends the hashes the server confirms
the motion by; the server runs the server GC, as i26-server does. Host upload is everything the server's socket sent
(LiteNetLib's count: its headers, acks and resends, not UDP/IP's 28 bytes a datagram); down is per client. Up per
client is 2.0–2.5 KiB/s (inputs with their hashes). Release build, a 12-core laptop, October 2026.

**Before and after the budget** (20 Hz, the default; "before" is the delta snapshots and interest management alone,
measured the same way but with four predicting clients):

| players | link | snapshot B | host upload | in pieces | snapshots lost | server tick (snapshots), ms | near / far cars: gap between states, ticks (mean, p99) |
|---|---|---|---|---|---|---|---|
| 16 | clean | 658 → 306 | 2.36 → 1.15 Mbit/s | 0 → 0 | 0 → 0 | 1.2 (0.5) → 0.9 (0.3) | 1.00, 1 / 2.00, 4 → 1.00, 1 / 1.92, 4 |
| 16 | 100 ms | 728 → 431 | 2.35 → 1.41 Mbit/s | 0 → 0 | 0 → 0 | 1.2 (0.5) → 0.6 (0.3) | 1.00, 1 / 2.14, 4 → 1.00, 1 / 1.63, 4 |
| 16 | 150 ms ±30, 5% | 657 → 356 | 2.11 → 1.15 Mbit/s | 0 → 0 | 4.6% → 4.9% | 0.9 (0.4) → 0.5 (0.3) | 1.07, 2 / 2.46, 8 → 1.07, 2 / 1.72, 5 |
| 32 | clean | 1121 → 542 | 7.81 → 3.84 Mbit/s | 3.8% → 0 | 0 → 0 | 3.2 (1.3) → 1.4 (0.7) | 1.00, 1 / 2.04, 4 → 1.00, 1 / 1.94, 4 |
| 32 | 100 ms | 1388 → 676 | 9.13 → 4.35 Mbit/s | 45% → 0 | 0 → 0 | 4.0 (1.8) → 1.1 (0.5) | 1.00, 1 / 1.99, 4 → 1.00, 1 / 1.76, 4 |
| 32 | 150 ms ±30, 5% | 1028 → 669 | 6.82 → 4.13 Mbit/s | 2.8% → 0 | 4.8% → 5.2% | 2.8 (1.0) → 1.0 (0.4) | 1.07, 2 / 2.29, 8 → 1.07, 2 / 1.78, 5 |
| 48 | clean | 1203 → 729 | 12.49 → 7.26 Mbit/s | 13% → 0 | 0 → 0 | 6.0 (2.2) → 2.0 (0.8) | 1.00, 1 / 2.17, 4 → 1.05, 1 / 2.08, 4 |
| 48 | 100 ms | 1688 → 932 | 16.64 → 8.60 Mbit/s | 77% → 0.2% | 0 → 0 | 6.8 (2.8) → 1.8 (0.7) | 1.00, 1 / 1.99, 4 → 1.07, 1 / 1.84, 4 |
| 48 | 150 ms ±30, 5% | 1558 → 1046 | 15.13 → 9.64 Mbit/s | 70% → 0 | 8.2% → 5.1% | 6.2 (2.3) → 1.8 (0.7) | 1.11, 2 / 2.42, 8 → 1.07, 2 / 1.75, 5 |
| 64 | clean | 1749 → 1062 | 24.16 → 13.79 Mbit/s | 84% → 0 | 0 → 0 | 11.2 (4.4) → 3.0 (1.1) | 1.00, 1 / 2.08, 4 → 1.05, 1 / 2.08, 4 |
| 64 | 100 ms | 2031 → 1223 | 26.58 → 14.89 Mbit/s | 97% → 0 | 0 → 0 | 11.4 (4.5) → 2.6 (0.9) | 1.00, 1 / 2.18, 4 → 1.08, 1 / 1.88, 5 |
| 64 | 150 ms ±30, 5% | 2155 → 1336 | 27.97 → 16.18 Mbit/s | 98% → 0 | 9.5% → 5.1% | 12.5 (5.3) → 2.8 (1.0) | 1.11, 3 / 2.54, 8 → 1.07, 2 / 1.86, 6 |

- **Where it went.** At 64 players a snapshot was 216 bytes of head (the own car and its 190-byte motion), 1092 of cars
  and 440 of rounds; now 48, about 750 and 250, and a snapshot never exceeds the datagram (1431 bytes on loopback):
  0–17 entries a snapshot are put off to a later one (more on the lossy links, where the acked baseline is older and
  the deltas bigger). The motion is confirmed by its hash in 84–96% of snapshots. Events went from 9–10 to 4–5 KiB/s
  per client, the scoreboard from 0.94 to 0.02.
- **The server's tick** (simulation and snapshots, 50 ms at 20 Hz) is 0.5–3 ms up to 64 players, at most 5 ms at the
  95th percentile and 8 ms at the worst in the 64-player runs; "before" wrote the snapshots on one thread without the
  server GC. A standalone run of 64 players (no clients, every one driving and firing, 1400-byte budgets) takes 1.8 ms
  of simulation and 1.1 ms of snapshots a tick.
- **Remote cars.** Near cars (within 200 m) still come every tick but one in twenty (the budget puts one off a tick);
  the longest any car waited was the two-second bound (in the runs traced, at the start, when every joiner's full
  snapshot competes for room).
  `CarTrack`'s tests drive a car round a 120 m circle at 30 m/s at the three tiers' rates with 10% loss, and at uneven
  intervals (1–5 ticks at 20 Hz, 1–9 at 60) with 5% loss: no frame's step more than half again its travel, the drawn car
  within 0.1 m of where it was at the drawn time.
- **Prediction.** Steady driving is still exact (the prediction tests: every compared tick bit for bit, 96–98% of the
  snapshots confirmed by hash). In the 64-car brawl the first four clients (the ones the "before" runs predicted)
  corrected 0–6 of about 1500 compared ticks up to 32 players (before 0–3), and 7–24 at 48 and 64 players (0.5–1.5%,
  before 0–4): crashes into other cars, which the prediction meets as the snapshots have them, now sometimes a tick or
  two older or at the coarse precision.

**60 Hz** (three times the snapshots; before → after):

| players | clean | 100 ms | 150 ms ±30, 5% |
|---|---|---|---|
| 16 | 4.84 → 2.16 Mbit/s | 5.34 → 2.59 | 5.14 → 2.92 |
| 32 | 16.43 → 7.70 | 19.44 → 10.55 | 18.19 → 11.05 |
| 48 | 30.57 → 14.79 | 35.34 → 22.54 | 32.98 → 23.68 |
| 64 | 49.52 → 25.75 | 36.50 → 36.68 | 42.39 → 40.74 |

Before, 64 players at 60 Hz went 42–77% in pieces and the server could not keep up (26 ms ticks of 16.7 with its 64
clients beside it); now none in pieces and 2 ms ticks (3 at the 95th percentile). The upload does not fall as far as at
20 Hz because a datagram a tick each is already 64 × 60 × 1431 bytes: at 60 Hz a game of more than 32 wants an upload
limit.

**An upload limit** (`--upload MBIT`, 64 players, 20 Hz):

| limit | link | host upload | near cars: gap mean / p95 / p99 | far cars: gap mean / p95 | prediction corrections |
|---|---|---|---|---|---|
| none | clean | 13.79 Mbit/s | 1.05 / 1 / 1 | 2.08 / 4 | 506 / 25530 |
| 10 Mbit/s | clean | 10.08 Mbit/s | 1.29 / 2 / 4 | 3.47 / 9 | 270 / 25472 |
| 10 Mbit/s | 150 ms ±30, 5% | 10.22 Mbit/s | 1.74 / 4 / 6 | 4.35 / 10 | 380 / 22805 |
| 6 Mbit/s | clean | 6.44 Mbit/s | 3.86 / 11 / 33 | 10.9 / 26 | 931 / 25532 |

10 Mbit/s holds 64 players with near cars every tick or two; 6 is too little (near cars every 4 ticks, a twentieth of
the snapshots over budget by their essential entries alone).

**End to end** (October 2026): `i26-server --arena m01 --max 64 --ai 8 --bots 55` (55 headless players from the
server's process, 8 AI cars) and a real client through `tools/shot.sh` in real time with `I76_NET_LAG=75,5,30`,
`I76_NET_CARS=1` and `I76_NET_DEBUG=1`: the server logged 15.3–15.8 Mbit/s of upload, snapshots of 1400 bytes, 0–2 in
pieces a minute and a 3.3 ms tick (28 at the worst). The client's 63 remote cars took 63,000 frame steps after it had
settled with no step more than twice its neighbours but at the instants the capture saved a picture (which stalls the
game); the prediction logged one correction over 1 cm (7 cm) in 460 compared ticks; the scoreboard paged 56 players,
Ctrl+T drew the nearest names, the radar every car.

**A home host** (a listen server, its host playing): `tools/shot.sh --host-listen --max-players 32` with
`i26-server --bots-to PORT --bots 31` (31 headless players on the same machine): 5.8 Mbit/s of upload once all had
joined (the players crowd the spawn points more than the load test's spread, so more cars are near and the snapshots
fuller: 910 bytes), the server's tick 3–6 ms beside the game's own frames (45 at the worst, a frame saving a picture).

**How many players.** The host's upload at 20 Hz (the default) and 60 Hz, the range over the three links (a listen
server's own player costs it nothing, being on loopback; add UDP/IP's 28 bytes a datagram, about 0.6 Mbit/s at 64
players and 20 Hz):

| players | 20 Hz | 60 Hz | download per client (20 Hz) |
|---|---|---|---|
| 8 | 0.4–0.5 Mbit/s | 0.8–1.1 Mbit/s | 0.05–0.06 Mbit/s |
| 16 | 1.2–1.4 Mbit/s | 2.2–2.9 Mbit/s | 0.07–0.09 Mbit/s |
| 32 | 3.8–4.4 Mbit/s (5.8 in the home host run) | 7.7–11 Mbit/s | 0.12–0.14 Mbit/s |
| 48 | 7.3–9.6 Mbit/s | 15–24 Mbit/s, or `--upload` | 0.15–0.19 Mbit/s |
| 64 | 13.8–16.2 Mbit/s, or 10 with `--upload 10` | 26–41 Mbit/s, or `--upload` | 0.22–0.24 Mbit/s |

- **The host form** offers up to 32 drivers (the original's went to 8; `NetForms.MaxDrivers`): 4–6 Mbit/s at 20 Hz,
  about 11 at 60, within a typical home upload with room for the rest of the house, and a tick of a few milliseconds
  beside the game's frame. The command line's `--max-players` goes to 64, and `--upload` limits a listen server too.
- **i26-server** takes `--max` up to 64 (`Protocol.MaxSeats`), 32 by default: 64 players need 14–16 Mbit/s at 20 Hz, or
  `--upload 10` for 10 with near cars every tick or two; at 60 Hz give it an `--upload` above 32 players. The server's
  tick stays under 5 ms (95th percentile) at 64 players at either rate.

### Tests

`tests/I76.Net.Tests`:

- **Round trips:** numbers, rotations, every message, a car's state
  (precision), and a big snapshot split into pieces and put back together.
- **The input queue:** order, repeats, holding, correction, a client
  running ahead.
- **The host, on m01** (skipped without an install):
  - cars are built from the server's own install;
  - slots are reused after a leave;
  - a car driven by nonsense every tick is no faster than an honest one;
  - a snapshot written into a client's mirror reads back as the server
    has it.
- **Damage on every client** (`DamageShowsOnTheAttackersAndTheVictimsClients`):
  two bots through the lag proxy (50 ms, 2% lost). One's hits, staged on
  the server, strip the other's front armour and reach a part. The victim as
  the attacker's client sees it (zone pools, engine, suspension, brake, the
  target bar) matches the server within a 255th. The victim's own client has
  every part exactly.
- **The original's rules** (`NetRulesTests`): RAISE_DEAD over the wire (refused at 3 s, nothing in 20 s, then the car
  back) with the client's predicted car dead and still meanwhile and following the new car after; Reconfigure refused
  while alive, for a car the server lacks, a restricted weapon and the same special twice, then a player's own variant
  taken and brought by the respawn on the server and both clients (the own car rebuilt in place, its build counted, the
  prediction on it); the boot by name and slot; the invite queue (skip, decline, accept, a timeout); the end's lines and
  event, the time limit's 10 s; the strict checks on stock cars; the new messages' round trips. `tests/I76.Sim.Tests/
  NetRulesTests`: what each death books, the end's blow-up −500, a leaver's wreck, the eighth team, the rebuild in place.
- **Co-op story missions** (`CoopNetTests`, T01 and Nitro's P01): three players then a late fourth, each with a mirror
  built as the game builds it: the opening cut-scene's camera and radio on every client, a non-host's skip ignored and the
  host's taken, an objective's change everywhere, a leave-map warning on that player's client alone and the put-back
  told, a death and the respawn 5 s later, the late joiner's accept with the objectives as they stand, a fifth refused, the
  mission's failure and its line on every client and the game over. Eight players (`EightPlayersShareAStoryMission`: eight seats and cars apart, a ninth refused, the end on all eight); `CoopFormationTests` places eight humans on every stock co-op start clear of objects and each other. The successor's restore and replay of a mission (a
  death and its respawn across it) hashes as the host's game; the host quitting mid-mission hands it to the lowest player
  and the mission goes on. The new messages' round trips. Each player's car (`EachPlayerDrivesTheCarTheyChose`): the first seat's
  remade in place, a later one's built on joining, "the mission's own" the story's whatever the first seat chose, the accept's
  seat cars, every mirror with them; and the successor's replay above with chosen cars, which hashes as the host's too.
  Movies and personal cameras (protocol 19): `AMovieStandsTheGameStillForEveryoneUntilTheHostsCopyIsOver` (the server's tick, the mission
  clock and the radio stand still and the clients' view with them; another player's copy being over, or their skip key, does not end it;
  a joiner has it; the host's copy ends it everywhere and the game goes on), `AMovieGoesOnUnderTheNewHostWhenTheHostLeavesDuringIt`,
  `AJumpCameraShowsOnTheJumpersScreenAlone` (T06: one player over a jump zone gets the script camera, the other never sees it and drives
  on); in `CoopCamerasTests` the survey of all 45 scripts (counts and pcs), a jump camera on its jumper alone (the other's keys work,
  the global camera stays off), and the leave-the-map camera ending with the put-back.
  `MissionTitlesTests`: the co-op lists' contents and order in both games.
- **Bots that play by the mode** (`NetBotTests`, the stock arenas; the Nitro ones need `I76_NITRO_INSTALL`): four race bots lap N26 in order and one wins by laps (the end names its
  slot); six CTF bots on N12 capture a flag and the capture is on the team's players; six team-combat bots on m02 kill the other side, kill no teammate and hit one only
  by a stray round; free-for-all bots hit the human and one another; the seats (bots leave for humans and come back, slots never meet, the events and the
  roster); a dead bot comes back after the 5 s; a hurt one regenerates (m07); the cars and names are stock on both installs; the AI fill attacks the human, not the bots; a
  client over the wire sees them on its scoreboard and the game ends; a successor host takes them over and replays to the host's state hash.
- **Headless clients** (`ThreeBotsPlayAMinuteOfMeleeOverALossyLink`): three headless
  clients against an in-process server, for 60 s of real time
  (`I76_NET_TEST_SECONDS` to change it), through the lag proxy at 100 ms
  ±10 each way and 2% loss.
  - One bot hunts from the snapshots (AI-derived steering and fire).
  - One drives the same way, then is staged as a kill by the hunter on the
    server.
  - One sends nonsense every tick and garbage messages, then leaves.
  - It asserts:
    - snapshots keep coming (85% or more);
    - the ack follows the client by about a round trip;
    - the kill scores by mass and the victim respawns;
    - the nonsense is corrected, its car is never faster than possible,
      and the server never faults;
    - the leave reaches the others;
    - the mirror is starved for under 10% of frames (5 frames in a minute).

- **Prediction** (`PredictionTests`):
  - `TheClientsStepIsTheServers`: a server (`MeleeHost`) and a client's
    predictor stepped with the same scripted drive for a minute (accelerate,
    steer, brake, reverse, a shift, the handbrake, the engine off and on,
    lights, the stick, a weapon cycle): at every acked tick the predicted
    car's `OwnMotion` is the server's bit for bit, at 20 and 60 Hz, with the
    snapshots at once and 6 ticks late (each replaying six inputs). A frame
    delta one tick off fails it (four ticks in five off, by up to 20 cm).
  - `ThePredictedCarAgreesWithTheServersOverALaggyLink`: a real server and
    client through the lag proxy, 50 ms each way, and 75 ± 30 ms with 5% lost:
    98% (95% with loss) of the compared ticks within 1 cm (measured: all of them
    exact); then the server moves the car 10 m and hits it, and the prediction
    snaps and is exact again within half a second.
  - `ASmallCorrectionIsDrawnAwayABigOneSnaps`: the server nudges the car
    0.5 m and 5°: right after the snapshot the car is drawn where it was (a
    0.5 m offset), and half a second later where the prediction has it; an
    8.5 m move is drawn at once.
- **The wire** (`WireTests`): the motion's round trip is exact, with and
  without its slow part, the contact's other party by id, the stale key
  ramps; the queue's slack.
- **Combat effects** (`CombatEffectsTests`; a server and headless clients
  without sockets, the snapshots and events a few ticks late):
  - `OwnShotsAreDrawnAtOnceAndTheServersAreNotDrawnAgain`: at 68 mph with the
    snapshots four ticks late, the machine gun's 30 shots are drawn each on the
    tick of its input, from the muzzle where the server then fires it (0.00 cm
    apart), and none of the server's shot events or rounds is drawn again; the
    server then destroys the gun: the 3 shots drawn before the client heard are
    taken back (rounds removed) and no more are drawn.
  - `AnOwnMineIsDrawnAtOnceThenTheServersTakesItsPlace`: the predicted mine is
    drawn at once, the server's replaces it when it reaches the view.
  - `FlamesShowOnEveryClient`: another player's stream on a client, from the
    muzzle of its copy of the car, as long as the server's; the own predicted
    one; both gone when the trigger is let go; without the fire predicted, the
    own one from the snapshot.
  - `TheChemicalCloudHurtsOnTheServerAndShowsOnTheClients` (Nitro, N01): the
    server's cloud hurts a player's car in it every tick (450 points in 10
    ticks), the victim's client gets the impacts, both clients the landed round,
    and the shooter's predicted round gives way to the server's.
  - `TheFireControlAndTheFlamesRoundTrip`: the new blocks' encoding.
- `tests/I76.Sim.Tests/ChemMortarTests.cs` (Nitro): the chemical round lands
  0.45 s and 38 m out from a still Piranha with no impact, lingers its 16.7 s;
  a car in the cloud takes the packet (45, flag 0x10) and the xclst1 splash every
  frame; a direct hit lands it under the car.

- **Delta snapshots** (`DeltaTests`):
  - `AByteDeltaComesBackExactly`: random byte strings against random baselines, longer and shorter, come back exactly;
    a delta that runs past its length is refused.
  - `EveryFieldOfACarGoesOnTheWire`: every public field of `CarState`, set at random, comes back within its precision
    (a field added to the car and left out of its channels and cold bytes fails here).
  - `TheClientsFrameIsTheServersThroughLoss`: an encoder and a decoder over 1500 ticks of a changing world (cars and
    rounds coming and going, respawns, damage, the own car, motion and fire control as real encodings, a flame stream), at 20 and 60 Hz, with 0, 20
    and 30% of snapshots lost, acks 4 ticks late and none for 100 ticks, with and without interest: after every
    snapshot the client's frame is the server's (every car's and round's channels, the own car's, motion's and fire control's bytes),
    a car sent that tick is the server's quantized car (a coarse one within half its coarse units), no snapshot is lost
    for want of its baseline, and full ones go only at the start and once the acks had stopped for 1.6 s.
  - `ASnapshotWithoutItsBaselineIsLostNotMisread`, the encoder's baselines (an ack of a tick never sent, one too old).
  - `FarCarsAreSentLessOften`: the tiers, the intervals at 20, 28 and 60 Hz, the staggering, the rounds.
  - `ARemoteCarIsDrawnSmoothlyWhateverItsRate` and `ARemoteCarSentAtUnevenIntervalsIsDrawnSmoothly` (above).
  - `SnapshotWritingCost` (with `I76_NET_BENCH`; `I76_NET_BENCH_SEATS`, `I76_NET_BENCH_BUDGET`): the server's time to
    write a tick's snapshots, and its simulation's.
- **The snapshot budget** (`BudgetTests.EverySnapshotFitsItsBudgetAndEveryCarGoesInTime`, m01): 48 players driving and
  firing for 15 s, each client's snapshots written within 700 bytes (at 20 and 60 Hz) and 1400 (20 Hz) and decoded
  with acks four ticks late: every snapshot within its budget but when its essential entries alone are not (at most one
  in a thousand), the client's frame always readable, every car the server has at every client within the first
  second, none unsent past the bound (two seconds; measured 21–24 ticks at 20 Hz, 61 at 60), and with a datagram's
  budget the cars within 50 m every tick in 99 updates of 100.
- **Many players** (`ScaleTests`): the names table (a batch's names once, then by number; a reader that never saw a
  name refuses it), which events reach whom, the scoreboard's deltas (64 rows 1.5 KB in full, a change in a few bytes,
  pings within 10 ms not sent, a player gone). The prediction tests over the lag proxy also check that 80% or more of
  the snapshots confirm the motion by hash (measured 96–98%) with no confirmation unresolved.
- **The load test** (`LoadTests.BandwidthAndCapacityUnderLoad`, only with `I76_NET_LOAD`): the tables above. For
  example `I76_NET_LOAD=8,16,24,32 I76_NET_LOAD_LINKS=clean,50,75:30:5 I76_NET_LOAD_OUT=load.md dotnet test
  tests/I76.Net.Tests --filter LoadTests`; also `I76_NET_LOAD_SECONDS`, `_AI`, `_ARENA`, `_RATE`, `_PREDICTED` (clients
  that also run a mirror and the prediction: all by default), `_UPLOAD` (the host's upload limit, Mbit/s), `_DELTA=0`
  and `_INTEREST=0`. Each row has the snapshot's size (and
  its head, cars and rounds), down and up per client, the host's upload, the share in pieces and pieces per snapshot,
  the snapshots lost and lost for a baseline, the full ones, the server's tick and its snapshot writing, the
  prediction's corrections (and the first four clients' apart), the gaps between each remote car's states near (within
  200 m) and far, the frames the view went without a newer snapshot, the events and scoreboard per client and the event
  kinds that cost most, the share of motions confirmed, the entries put off a snapshot and the snapshots over budget.

- **Everyone sees everyone** (`VisibilityTests`), three headless clients against a server, each building its mirror
  1.5 s after its accept as the game's loading screen does:
  - `EveryoneSeesEveryoneAfterANewRound`: the testers' "fine at first, then nobody can see anyone". A round ends and the
    next begins; each client's old mirror runs on 0.3 s, as the old scene does, before the next is built. It took the new
    round's first events, the spawns of the players seated after it, and lost them: the first player (a listen host) saw
    nobody in every round after the first, the second only the first. With a player's own variant, and a rejoin after.
  - `EveryoneSeesEveryoneThroughJoinsLeavesAndRejoins`: 75 ± 20 ms each way, 5% lost; joins, leaves and rejoins in
    several orders; one client throws away every spawn event and learns each later car by asking.
  - a car this install cannot build drawn as a stand-in; a view throwing on one spawn losing no other event; the query
    and its answer (cars in the arena only, rate-limited).
- **Variants** (`VariantTests.ACarInTheServersAddonFolderGoesWithItsFile`): a car in the server's addon\ goes with its
  file, and a client without it builds the same car.

`tests/I76.Sim.Tests`: a key held through a respawn drives the new car.

`tests/I76.Net.Tests/DiscoveryTests.cs`:
- the session answer's round trip, and stray data not taken for one;
- the address book's addresses (`host`, `host:port`, `[v6]:port`) and bad ones;
- a saved variant with no configuration playing as its model's first, and a listen server not
  taking the host's overlay;
- a running server answering a query at its address and a LAN search, its
  answer following a join.

`tests/I76.Net.Tests/MapTransferTests.cs` (custom maps):
- file names (paths, "..", devices, extensions), stock names, the hash (order and case free, every byte counts), the
  four messages, the accept's hash, the session answer and the anet tag's flags round trip; offers a client must not act
  on (a path, a name twice, 17 files, a hash not covering the list); a download put together in pieces, pieces out of
  place or past the end refused, one cut off taken up from where it stopped, a file failing its SHA-256 never used, a
  cached copy changed afterwards not taken; the caps;
- on an install (I76_INSTALL), with a synthetic custom map made from m01 in a scratch install of links (its mission
  file renamed zzmap.lvl, ZONE and WRLD naming zzmap.ter and zzmap.npt): the file set is those three files; a server with
  it and two clients, one without the map and one with another zzmap, both downloading at once on a slow rate, joining,
  and building their arena from their cache folder (B's own copy fails the fingerprint, the cache's passes); a third visit
  finds it cached; a client cut off at two pieces takes it up from there; a server not sharing says so and refuses a
  request anyway; a stock arena offers nothing;
- with I76_CUSTOM_MAPS and I76_NITRO_INSTALL: the pack's canyonlx.rac (its terrain in addon\) sent and rebuilt.

End to end (October 2026): `i26-server --install SCRATCH --arena xfermap.lvl --map-rate 64` (a scratch install of links
with a synthetic map in addon\) and `tools/shot.sh --connect 127.0.0.1:PORT` from the stock install with a scratch user
dir: the prompt (`I76_LOADING_ANSWER=0`), 186 KiB downloaded in about two seconds, the game played on the downloaded map;
again from the cache without a prompt; Cancel (`I76_LOADING_ANSWER=2`) back with "the map download was declined"; a
`--host-listen` game on the map joined mid-game by a client with `--map-downloads always`.

End to end (October 2026, delta snapshots): `i26-server --arena m01 --ai 8` and a client through `tools/shot.sh` in
real time with `I76_NET_LAG=75,5,30` and `I76_NET_CARS=1` (a line a frame for each remote car): the eight AI cars, all
attacking the one player and so all at the full rate, drawn without a step more than twice what their speed allows
(about 6 ms frames). Then two clients on one server with two AI cars: the other player's car and the AI went between
the full, half and low rates (intervals 1, 2 and 4 ticks) as they came and went, with no pop (the steps the check
flagged were a wreck falling and the frames after a stall of the game itself).

End to end (October 2026, the original's rules): `i26-server --arena m01 --ai 2 --bots 2` (later `--restrict gclight`) and
a client through `tools/shot.sh` in real time: Ctrl+Del killed it ("*** Probe has been killed", the banner counting down
"Space in 5 s"); Space at 3 s was refused (the server's log: "too soon (3.1 s)"); `'` held showed the score window;
Enter opened Reconfigure, the ABX AMZ was refused ("Restricted weapons: 20mm Cannon" over the form), the Courcheval
Cavera taken ("You will come back in the Courcheval Cavera (TR)."), and Space brought the car back as it; the bots came
back by RAISE_DEAD 5–6 s after their deaths. The console's `kick 2` sent a client joined from the shell back to its
STANDINGS with "The game ended: The host booted you from the game.". A `--host-listen --invite-only` host with
`i26-server --bots-to PORT --bots 2`: "Let bot1 join the game? (CTRL-Y / CTRL-N)", Ctrl+H to bot2, Ctrl+Y let it in,
Ctrl+N turned bot1 away; with `'` held the window showed "H Host" and "1 bot2"; Ctrl+1 asked "Boot bot2 from the game?
(CTRL-Y / CTRL-N)" and Ctrl+Y booted it.

`tools/lan-test.sh OUTDIR [seconds]` plays the menus end to end on one
machine, two games through `tools/shot.sh` (each its own display and scratch
user data):
- one hosts: MELEE → MULTI MELEE → HOST → IPX → BROADCAST GAME;
- the other joins: MELEE → MULTI MELEE → JOIN → IPX, held until the roster
  lists the game (the shell script's `wait:games=1`), the first row, JOIN
  GAME;
- `I76_NET_TEST=2,seconds` holds each game's capture count until both
  players are on the scoreboard, saves each view as `_game.png`, and ends the
  host's melee after that many seconds;
- both come back to their STANDINGS, captured as the run's screenshot.

The script checks the logs: the host's listen server, the joiner's join to
the second seat, both seeing two players, and both back on two standings
rows.

`I76_NET_HURT=N` on a `--host-listen` game is for captures of damage. It
sets the nearest other car down 30 m ahead of the host's and makes it the
host's radar target. Then every N server ticks the server lands a round's
hit on that car's front. A `--connect` game beside it (with
`I76_AUTO_TARGET=1`) shows the same hits on its own damage panel. For
example, `I76_NET_HURT=30 I76_SHOT_EVERY=140`, the host started about ten
seconds before the joiner.

## Lobby servers (anet)

The community still runs Activision's anet game server, built from the LGPL
anet 0.10 source, for the Nitro Pack's internet games. I26 uses those
servers to **list** its games and to **introduce** joiners to hosts, so a
host needs no port forwarding behind most home routers. It never plays with
the original game (its netcode is a different thing altogether), and the server carries no game traffic: it is
a lobby and an introducer, as it always was.

### Servers

The address book's entries are anet game servers (UDP port 21157 unless the
entry gives another). A new book holds these:

| Name | Address | Run by |
|---|---|---|
| BZ911 | alink.bz911.net | the Battlezone community (911.alink.bz911.net is the same machine) |
| Glenrio | glenrio.interstate76.com | interstate76.com |
| Interstate76.com | play.interstate76.com | interstate76.com |
| Iondriver | alink.iondriver.com | (listed on interstate76.com) |

interstate76.com's two servers joined the default book in 0.8.0; a book saved before then gets them once
(`user://internet_i76com.done` marks it), so a player who removes them keeps them removed. battlezone1.net, once
listed, no longer answers.

The internet roster lists the games of every server in the book at once, under a header row per server ("From the main
menu", "The internet roster"). Adding and removing servers is the CONNECT TO SERVER popup's NEW, editing and DELETE,
opened from the roster's SERVERS or by HOST → INTERNET. `i26-server --anet HOST[:PORT]` (repeatable)
lists a dedicated server; `--host-listen --anet HOST` a listen server from
the command line; `--join-anet HOST [--session NAME]` joins a listed game
(the one named, else the first).

### Species

A game logs in with its *species* (the session type); each species has its
own session table on every server, created on demand (dp_addClient), so a
server needs no setup for I26 and other games never see its sessions. I26's
is **0x4926 = 18726** (`AnetSpecies.I26`, one constant): "I" and 26.
Activision assigned species from one registry, small numbers in shipping
order (the Nitro Pack 604, the anet demos 64 and 65, the docs' examples 1103
and 1234); 18726 is far above them and below 0x8000, since some packets
carry the type as a signed short. Never 604: I26 games would show in real
Nitro players' lists, where joining fails.

Both games share the species; the game (I76 or the Nitro Pack) is a flag in
the session's user bytes, and each lists only its own. One species keeps the
servers' status pages to one line and the operators to one request.

### The protocol subset (`src/I76.Net/Anet`)

A C# port of what a lobby client needs from anet's `dp` (`h/dppkt.h`,
`h/dptab.h`, `src/dp/dpio.c`, `dptab.c`, `dp2.c`), checked byte for byte
against the real server (`AnetWireTests`):

- **dpio** (`DpioLink`): every packet starts `'d'` and a letter. SYN `dY`
  (a random first packet number; its body is version 5, the address length 6
  and the sender's address: I26 leaves out the optional destination, so a
  server behind a 1:1 NAT, which compares that with its own idea of its
  address, does not drop it), DATA `dT` (u16 number, u8 length, at most 250
  bytes, 32 unacknowledged), ACK `dU` (the packet and how far before it
  everything has come), FIN `dF`, pings `dB`/`dC` every 5 s (keeping the NAT
  mapping open; the server drops a client silent for 41 s, I26 gives up on a
  server after 30), gathers `dG`. Retransmission starts at 0.5 s and doubles
  to 4.1 s. A peer's SYN carries the address it sends to: the server's tells
  I26 its public address.
- **Login**: `e1` and the species (little-endian), reliably. The server
  subscribes the client to that species' table and takes its MYSESSIONS
  table as a publisher.
- **dptab**: table items in small packets `d%` (hops, subkey length, key,
  then length, subkey and bytes per item, length 255 deleting), large ones in
  `d!` + `d@` checked by `dp_crc32` (CRC-32, polynomial 0x04C11DB7 not
  reflected), deletes `d*`. Only the species' table `[1, lo, hi]` is read;
  the servers table, the apps table (patch downloads: never acted on) and
  account traffic are ignored.
- **The session record** (`AnetSession`): the host publishes the 83-byte raw
  `dp_session_t` in its table `[2]`; subscribers get the compact form (max
  players, players, flags, name, password, user bytes with trailing zeros cut,
  the master address if it is not the subkey's). The subkey is an address and
  a random karma. The original puts the host's address in both; I26 puts the
  host's own (private) address in the subkey and the public one the server
  sees as the master address, so a joiner behind the same NAT finds the host
  at home. The flags are ENABLE_NEWPLAYERS (while it takes joins) and
  MIGRATE_FORBIDDEN.
- **The user bytes** (`I26SessionTag`, 14: dwUser1 then szUserField): 0x26,
  the I26 protocol version, a flags byte (Nitro, team play, invite only, over,
  the kind, CTF teams), the arena's stem (8) and an extension code (bit 7: a custom map), the goal (bit 4: the map is
  shared, bit 5 a password, bit 6 Hidden Weapons & Cars allowed, bit 7 bit 6 is said). A host behind a filtering NAT
  never answers the roster's direct query, so the listing is all a joiner knows of it: in 0.5.0 it lacked the hidden
  rule, and every joiner's own check refused hidden cars there as if the host had it off. A 0.5.0 host's tag leaves
  bits 6-7 clear; the roster then leaves the rule to the host (`SessionInfo.MayBringHidden`).
  The roster lists only games of its own protocol version and game.
- **Introduction**: the joiner sends INDIRECT_JOIN `dI` (the session's
  subkey); the server sends the host REQUEST_OPEN `dX` with the joiner's two
  addresses, the one it sees and the one the joiner reported.

### On the game's socket

The punch only helps if the anet conversation uses the game's own UDP socket:
the server sees, and tells the other side, that socket's NAT mapping.
`AnetLayer` (a LiteNetLib `PacketLayerBase`) shares it:

- **In**: a datagram from a server being talked to that starts with `'d'`
  and a letter is anet's and is taken out before LiteNetLib reads it. The
  source decides, not the first byte alone: LiteNetLib's first byte is the
  packet property in its low five bits, the connection number in bits 5–6
  and the fragment flag in bit 7, so `0x64` is a Ping of connection number 3,
  which LiteNetLib does send.
- **Out**: LiteNetLib 2.1.4 has no public raw send; anet datagrams go out as
  unconnected messages and the layer strips LiteNetLib's one-byte header on
  the way (a thread-static flag around the call marks them; the layer runs
  synchronously in `SendRaw`). No fork of LiteNetLib.

The listen server and i26-server (`NetMeleeServer.ListOn`), the joiner
(`MeleeClient.ConnectVia`) and the roster (`SessionBrowser.List`) each have
one. The anet work left the game traffic's wire unchanged (protocol 10; custom maps made it 11, passwords 12).

### The hole punch

The punch datagrams are LiteNetLib unconnected messages, `"I26P"`, a kind,
the session's karma and a nonce (`HolePunch`); an older I26 ignores them.

1. The host lists its game and keeps the link up (`AnetHost`).
2. The joiner logs in from its game socket, sends INDIRECT_JOIN, and probes
   the host's public address and its own address every 0.2 s (`AnetJoiner`).
3. On REQUEST_OPEN the host probes both of the joiner's addresses every 0.2 s
   for 10 s, and answers every probe for its session. It also probes the 8
   ports either side of the one the server saw (`AnetHost.PredictSpan`): a
   symmetric NAT that hands out ports in sequence maps the joiner's probes to
   the host a step or a few on, and the probe that lands there opens both NATs.
4. Each side's outgoing probe opens its own NAT toward the other; with
   endpoint-independent mappings the port the server saw is the one the
   other side aims at, so the next probe gets through. The first address that
   answers with the session's karma is where the joiner connects with
   LiteNetLib, as before.
5. Behind the same NAT the host's own address answers. A host whose port is
   forwarded (or opened by UPnP outside I26) answers the first probe, before
   the server has done anything; with the server down, a listed game is still
   joined that way.

What the player sees: each server's header row on the roster says "connecting...", "not answering" or "not found" while it
has no games (the box above, "Contacting servers..." while every one is still being reached); the highlighted game shows
its ping when it answers I26's query directly (a host behind a filtering NAT
does not, until introduced); the loading screen says "Joining via server...",
then "Connecting to the host..."; a failed punch first asks another server
of the address book where it sees the joiner ("Checking your connection...",
up to 4 s): a different port than the first server saw is a symmetric NAT on
the joiner's side, the same port puts it on the host's. The form then says
whose router stopped it and the port the host can forward
(`AnetJoiner.FailureText`), e.g. "Could not join: your router gives every
connection a new port (a symmetric NAT, ...). The host can forward UDP port
7604 to their computer, or join from another network.". The
host's HUD reads "Listed on SERVER", "Contacting SERVER..." or "Not listed:
SERVER is not answering" (the game runs and takes direct joins either way).

### Limits

- **Symmetric NATs** (a new mapping per destination: some corporate and
  mobile networks) and **CGNAT** with such mappings still fail unless they
  hand out ports in sequence (step 3): the port the server saw is not the one
  the other side would need. A symmetric host is still joined by players
  whose own NAT does not filter (the host's probes reach them from any port),
  which is how three can join and a fourth cannot. anet's README2 had the
  same limit ("only those that allow 'party line' use of an open port"). The
  next step is a relay (each side sends outward to a relay, which forwards);
  a host's port forwarding or UPnP works meanwhile.
- Seats and players fit the listing at 64 and beyond (a 16-bit field in the host's raw record, a byte in the server's compact one).
- The server is volunteers' 1998 code; a silent one is retried (2 s, doubling
  to 30 s) and said so, nothing more.
- No anet host migration (I26's own is above, "Host migration"), no accounts. A password (above) is checked by the host, not the lobby server. The listing shows the
  arena, game type, players and seats; the score limit and the drivers come
  only from the host's direct answer.
- UPnP is not done: hole punching covers the common routers, and a port
  mapping without asking is a choice for the player.

### Testing against the real server

Never against the community servers. The anet server builds from the LGPL
source as 32-bit (its session record has `long` fields and an 83-byte size
check), with `pkgsi686Linux.stdenv.cc` first on PATH and `-fcommon -w
-std=gnu89`; two small local changes make it bind `ANET_BIND` (uudps.c,
sbdserv.c) and move its crash-report TCP port to `ANET_SBDPORT`. Run it as
`ANET_BIND=127.0.0.1 UUDP_HOSTNAME=127.0.0.1 ANET_SBDPORT=21158 server
-p=21157 -R=rooms.txt -H=servers.txt -A=apps.txt` (a rooms file `1234 Test
Room`, a servers file `127.0.0.1 localhost`, an empty apps file).

- `tests/I76.Net.Tests/Anet`: the encodings against bytes the real server
  and a real anet client exchanged (`AnetWireTests`); listing, publishing and
  joining through the server end to end over loopback (`AnetPunchTests`,
  bare LiteNetLib peers; `AnetMeleeTests`, a `NetMeleeServer` on m01 and a
  `MeleeClient`), against `FakeAnetServer` (in process, the parts of the
  server a client meets) and, with `I76_ANET_SERVER` naming the binary, the
  real one.
- `NatBox` emulates NATs without root: a machine "behind" one has a
  `NatTunnelLayer` that sends everything to the box, which sends it on from
  a public socket on its own loopback address (127.0.0.2, ...), one per inside
  machine (endpoint-independent) or per machine and destination (symmetric),
  filtering by address and port (`SymmetricSequential`: consecutive ports).
  Results, the same on both servers: two endpoint-independent NATs punch
  through; a sequential symmetric NAT is reached around the seen port; seven
  joiners (two sharing a NAT, one sequential symmetric, three at once) all get
  in while the earlier ones stay connected; a random symmetric NAT on the
  joiner's side fails, blamed on the joiner's router when a second server sees
  another port, on the host's when it sees the same; two machines behind one NAT join at the host's private address (the
  box does not hairpin); a host whose server is silent says "Not listed" and
  still takes a joiner directly.
- `tools/anet-test.sh OUTDIR` plays it through the shell: the server on
  127.0.0.1:21157, one game hosting from HOST > INTERNET, another's roster
  captured listing it (and its popup), a third joining from the roster. On
  one machine with no NAT between them the host answers the joiner's first
  probe, before the server's introduction (the NAT cases are the tests
  above). For the Nitro Pack: `I76_ANET_NAV="click:440,300@8;click:390,350@12"`,
  `I76_ANET_BOOK="click:284,180@24;click:366,415@26"`, `I76_ANET_ARROW=212,197`,
  `I76_ANET_HOST_IN="$I76_ANET_NAV;click:380,375@16;click:410,402@20;$I76_ANET_BOOK;click:300,462@30"`,
  `I76_ANET_ROSTER_IN="$I76_ANET_NAV;click:430,375@16;click:410,402@20;wait:games=1@28"` and
  `I76_ANET_JOIN_IN="$I76_ANET_ROSTER_IN;click:100,197@32;click:300,462@34"` (the roster's first row is the server's header,
  the game the second). Both games passed (October 2026), before the roster searched the whole book; every book the script
  writes holds the local server alone.
- `ServerBrowserTests`: the roster's search of a whole book against two `FakeAnetServer`s, a silent socket, a name that is
  not found, a game at its own address and an entry that is no address: each group's games and state, the silent one
  holding up nobody, a game two servers list under both (each copy through its own server), and an edited book keeping the
  searches that stay.

## Left for later phases

- **Net-2:** (own-car prediction, reconciliation and smoothing, the own shots drawn at once, delta snapshots and capped
  extrapolation are done, above)
  - lag compensation for hitscan-like fire;
  - a jitter buffer that adapts the 100 ms delay;
  - event names interned rather than sent as strings.
- **Net-3:** listing and NAT punch-through are done through the community's
  anet servers (above, "Lobby servers"); left: a relay for symmetric NATs and
  CGNAT. LAN discovery and
  the listen server from the host form are done (above).
- **Net-4:**
  - input rate limits (kicks are done: the host's BOOT keys and `i26-server`'s `kick`, above);
  - session tokens and packet keys;
  - server-side logs and replays for review.
- **Net-5:** the host and join forms, CHOOSE A TEAM, the roster, the game's
  popup and STANDINGS are in (above). Left: HD upscales of records 18–23
  (the HD look falls back to the originals), the original's in-game roster
  and scoreboard in place of the overlay, and a STAND BY... wait (bank 36
  sprite 18) for a join that takes long.
- **Handguns in a network melee.** The server gives no car its driver's
  handguns, so the cockpit's raised arm has nothing to fire.
- **Chat text is not checked beyond control characters.** Invalid UTF-8
  arrives as replacement characters.
- **`World.Player` on a server** stays null. A client's mirror sets it to
  its own car.
