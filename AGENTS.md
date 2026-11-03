# Working on I26

Notes for coding agents and new contributors: what the codebase expects, how to check your work, and the traps that cost
time before. Read [CHARTER.md](CHARTER.md) first; it is short.

## Layout

| Path | What it is |
|---|---|
| `src/I76.Formats` | readers for the game's files (archives, models, textures, missions, input maps, ...). No Godot. |
| `src/I76.Sim` | the simulation: vehicles, weapons, damage, AI, mission scripts, the trip. Deterministic, no Godot. |
| `src/I76.Net`, `src/I76.Server` | network melee: snapshots, input queues, the wire protocol, the dedicated server. |
| `game/` | the Godot 4.7 .NET project: rendering (modern look and 1997 look), sound, input, the front end (`Scripts/Shell`). |
| `tests/` | xUnit: `I76.Sim.Tests`, `I76.Net.Tests`, `I76.Editor.Tests` (no install needed), `I76.Formats.Tests` (skips without an install). |
| `src/I76.Editor` | the map editor's logic (document, brushes, undo, saving through the map writers). No Godot. The editor's windows are `game/Scripts/Editor`; see docs/map-editor.md. |
| `tools/` | `I76.Cli` (inspect files, run a mission headless), `I76.Subtitles` (transcribe cutscenes & dialogue audio), `shot.sh` (off-screen captures), `release.sh`, asset fetchers. |
| `docs/` | the design notes and specifications the code follows: `architecture.md`, `deviations.md`, `frame-rate.md`, `multiplayer.md`, ... |

The sim never touches Godot; the game layer reads sim state and draws it. Keep that split: gameplay rules go in `src/`,
presentation in `game/`.

## Build and test

Two ways to get a toolchain; use either. **Plain:** the .NET 8 SDK and the Godot 4.7.x .NET editor (`godot` or `godot-mono` on
PATH, or `GODOT=/path/to/godot` for `tools/shot.sh`); see [DEVELOPMENT.md](DEVELOPMENT.md). **Nix (optional, Linux x86_64):**
the flake's devShell has the SDK, Godot and the script tools; enter it with `nix develop`, or prefix commands with
`nix --extra-experimental-features 'nix-command flakes' develop -c`. The commands below are the same either way (they say
`godot-mono`; use your binary).

```
export I76_INSTALL=/path/to/Interstate76          # the folder holding I76.ZFS (a legally owned copy)
(cd game && dotnet build)                          # the game's assemblies; do this before running or capturing
dotnet test I76.sln                                # all tests; Formats tests skip without I76_INSTALL
dotnet test tests/I76.Sim.Tests --filter Name~Salvage
dotnet run --project tools/I76.Cli -- run t01 30  # a mission headless for 30 s, printing what its script does
godot-mono --path game -- --install "$I76_INSTALL" --mission t01 --drive
```

Nitro Pack tests read `I76_NITRO_INSTALL` and skip without it. Game command-line options (after `--`) are listed at the
top of `game/Scripts/Boot/InstallLocator.cs`: `--mission`, `--drive [car.vcf]`, `--chase` (missions start in the cockpit, as the original's), `--melee`, `--1997`,
`--modern-physics`, `--seed`, `--skip-movies`, the network ones, ...

## Checking visual changes

`tools/shot.sh OUT.png [game args]` renders off-screen on Linux (a private Weston on the GPU, or Xvfb with software Vulkan; the Godot binary is `$GODOT`) with
its own scratch user data, and saves frame N-2. Read its header. The useful knobs:

- `I76_SHOT_SIZE=1280x720`, `I76_SHOT_FRAMES=N`, `I76_SHOT_SERIES=K` (the last K frames, for flicker), `I76_SHOT_FPS=60`.
- `I76_CAP_CAM="dx,dy,dz,yaw,pitch"` fixes the camera relative to the player's car; `I76_FREECAM=x,y,z,yaw,pitch` in world units.
- `I76_GAME_KEYS="f2@150;a@60-200"` presses game keys on frames; `I76_SHELL_INPUT="key:escape@60;move:250,215@90;click:250,215@95"`
  drives the menus (shell coordinates, 640x480); `launch:trip,t16,vppirna1.vcf@5` starts a trip mission from the shell.
- `I76_HOLD_HARDPOINT=N`, `--hold-throttle`, `--hold-fire`, `I76_MELEE_OPPONENTS=a.vcf,b.vcf`.

Every `I76_*` hook is listed in [docs/dev-hooks.md](docs/dev-hooks.md). Check a visual change in both looks
(default and `--1997`), and look at the image: "the build passed" is not "it looks right".

## Rules

- **Faithful by default.** Behaviour follows the original as specified in `docs/`. Every deliberate difference gets a
  row in [docs/deviations.md](docs/deviations.md), with what the original does and what we do. If a player says it
  plays unlike the original, check the original before defending the spec.
- **No game data in the repo.** No assets, extracted or upscaled art, sounds, fonts, game text tables or files derived
  from them. The game reads the player's install at runtime.
- **Never write into a game install.** For tests that need extra files, build a scratch folder of symlinks to the install.
- **User data is the player's live data** (settings, bookmarks, the trip car). Test against a scratch
  `XDG_DATA_HOME`/user dir; `shot.sh` already isolates it. Migrate settings forward, never overwrite them with defaults.
- **The sim is deterministic.** No wall clock, `Random` without the sim's seed, or Godot calls in `src/I76.Sim`. Where
  clients need new state it also goes into the net snapshot; any wire change bumps the protocol version
  (`src/I76.Net/Protocol.cs`).
- **New sim state must be serialized** — the snapshot guard test enforces it ([docs/savestate.md](docs/savestate.md)):
  save it in its class's `Save`/`Load` or mark it structural, derived or transient with the reason, and bump
  `SnapshotHeader.CurrentVersion`. `StateHash` is the snapshot's hash and follows by itself.
- **The tick is a setting.** `SimRate` per session: 20 Hz by default (102/2048 s), also 25/28/30/40/60. Never use exactly
  0.05 s: the original's substep and weapon timing sit on that edge. Per-frame rules of the original run per tick,
  which is what makes the rate setting reproduce the original at that frame rate ([docs/frame-rate.md](docs/frame-rate.md)).
- **Comments** follow the surrounding code: brief, factual, saying what the original does or why we differ.

## Workflow

- One change per git worktree and branch (`git worktree add ../i76-godot-wt/NAME -b fix/NAME main`); build, test and
  capture there; merge to main when the full suite passes.
- Tests: add one for every bug fixed and every rule implemented, ideally from measured numbers.
- The release: `tools/release.sh VERSION` (the maintainer's Nix-based script; manual export steps are in DEVELOPMENT.md) builds `dist/i26-VERSION-windows.zip` and `-linux.zip` and checks that no game
  file or user data got into the export.

## Pitfalls

- **Build `game/` before `shot.sh`**, or the game cannot instantiate its scripts and the capture waits for its timeout.
- **Mission intros swallow scripted keys** (the script holds the player). Use `--melee --mission m01 --drive` for
  captures driven by `I76_GAME_KEYS`.
- **Exported builds differ from the editor.** Imported textures come back from the GPU: never call
  RenderingServer-synchronous APIs (`Texture2D.GetImage`, ...) from worker threads while the main thread waits for
  them (it deadlocked a release on the loading screen). Runtime texture compression exists only in editor builds.
  `release.sh` rebuilds the dev assemblies after exporting; a bare export leaves the editor without game classes.
- **Linux pads over Bluetooth:** every rumble call is a blocking force-feedback upload; send rumble only on change,
  rate-limited (`ThrottledRumble`). Pad hot-plug can fire repeatedly; keep it cheap.
- **Shell habits:** `pkill -f PATTERN` can match your own shell; never leave a command waiting on stdin; long runs
  (exports, captures) need generous timeouts.
- **Godot scene reloads re-run `_Ready`:** one-per-process things (the boot splash overlay) need a static guard.
- **Coordinates:** game axes are mirrored in Z against Godot's (`Coords.ToGodot`); radar, cameras and turrets have
  each been bitten by it. Test directions with a contact placed clearly ahead-right.
