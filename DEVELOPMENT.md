# Developing I26

I26 is a .NET solution (`I76.sln`: the libraries `src/I76.Formats`, `src/I76.Sim`, `src/I76.Net`, `src/I76.Server`, `src/I76.Editor`, the CLI `tools/I76.Cli`, the subtitle tool `tools/I76.Subtitles`, and the tests) plus the Godot 4.7 .NET project in `game/`. The libraries and tests need only the .NET SDK, not Godot. [docs/README.md](docs/README.md) indexes the documentation, [docs/dev-hooks.md](docs/dev-hooks.md) lists the environment variables, [AGENTS.md](AGENTS.md) the practical notes (layout, pitfalls) and [CONTRIBUTING.md](CONTRIBUTING.md) the ground rules.

## Building from source

### Prerequisites

1. The **.NET 8 SDK** (`global.json` asks for 8.0.100 or a later 8.0 feature band).
2. **Godot 4.7.x, the .NET (mono) edition** (not the standard build). The project uses `Godot.NET.Sdk/4.7.2`; keep the editor on 4.7.x. Only needed to run or export the game, not to build or test the libraries.
3. Your own copy of Interstate '76 (and the Nitro Pack for its content). Set `I76_INSTALL` to the folder holding `I76.ZFS`, and `I76_NITRO_INSTALL` to the Nitro Pack's folder. Both are optional for building; tests that read game files are skipped without them.
4. Optional free assets (CC0 effects and ground textures), fetched rather than committed:

```
tools/fetch-vfx.sh       # into game/vfx
tools/fetch-ground.sh    # into game/ground (about 40 MB; I76_GROUND_RES=2K for the 2K sets)
```

They need `bash`, `curl` and `python3`. On Windows run them from Git Bash or WSL, or download the files by hand (the URLs are at the top of each script) and unpack them into those folders as the scripts do. The game falls back to original sprites and a procedural ground without them. After fetching, a release export needs `godot --headless --path game --import` so Godot imports them.

macOS is not an official target: it may work, but it is untested.

### Build and run

```
export I76_INSTALL=/path/to/Interstate76          # Windows: set I76_INSTALL=C:\Games\Interstate76
dotnet build I76.sln                               # the libraries, CLI, server and tests
dotnet build game/I76.Game.csproj                  # the game's assemblies (needs no Godot install)
```

Then open `game/project.godot` in the Godot .NET editor (build there with the Build button or Alt+B) and press F5, or run from a shell:

```
godot --path game -- --install "$I76_INSTALL" --mission a01
```

The executable may be called `godot`, `godot-mono`, `Godot_v4.7-stable_mono_linux.x86_64`, or `Godot_mono` on macOS. Game options come after the `--`; the list is at the top of `game/Scripts/Boot/InstallLocator.cs`.

**Pointing the game at an install.** Any of: `--install DIR` after the `--`; the `I76_INSTALL` environment variable; or build/export the game into the game folder and run it from there. To run from the editor (F5), set `I76_INSTALL` in the environment you start the editor from; to pass other game options use the shell form.

### With Nix (optional, Linux x86_64)

The flake's devShell has everything: .NET 8 SDK, Godot 4.7 mono and the tools the scripts use (Weston, ImageMagick, jq, ...).

```
nix --extra-experimental-features 'nix-command flakes' develop      # a shell; or prefix commands with `develop -c`
export I76_INSTALL=/path/to/Interstate76
dotnet build I76.sln
dotnet test I76.sln
(cd game && dotnet build) && godot-mono --path game -- --install "$I76_INSTALL" --mission a01
```

## Tests

```
dotnet test tests/I76.Sim.Tests      # no install needed
dotnet test tests/I76.Net.Tests      # no install needed (the Nitro and bot tests skip without one)
tools/test-net.sh                    # the same, failing on a short run (a crashed test host still prints "Passed!" for the tests it got through)
dotnet test tests/I76.Formats.Tests  # most tests need I76_INSTALL and are skipped without it
dotnet test tests/I76.Editor.Tests   # the map editor's brushes, undo and saving; no install needed
dotnet test tests/I76.Game.Tests     # the game layer's logic that needs no engine
```

A few network tests time a game over a simulated bad link against the wall clock (`[Trait("Load", "Sensitive")]`) and can fail on a busy machine. Day-to-day runs leave them out with `--filter "Load!=Sensitive"`; a release check runs everything, on a quiet machine.

The Nitro tests also read `I76_NITRO_INSTALL`; the custom map tests also need `I76_CUSTOM_MAPS`, an unzipped community map pack ([docs/dev-hooks.md](docs/dev-hooks.md)). The tests read only what the games ship: maps a player added to an install (even under a stock name, such as a community `t01.cbt` in the Nitro Pack) and files the games write as they run are left out (`MapFileSet.IsStockMission`), so a played install tests as a fresh one. A GitHub Actions workflow builds the solution and the game project and runs the test projects without an install. `dotnet run --project tools/I76.Cli -- --install DIR verify` checks the readers against an install. `dotnet run --project tools/I76.Cli -- installer info|list|extract SETUP.exe [DIR]` reads a GOG offline installer.

## Exporting a release

By hand, on any OS: install the Godot 4.7 mono export templates in the editor (Editor > Manage Export Templates), run the asset fetch scripts, import (`godot --headless --path game --import`), then export with the presets in `game/export_presets.cfg` (Project > Export), and add `tools/release/README.txt` and `tools/release/THIRD-PARTY-NOTICES.txt` to each zip.

`tools/release.sh VERSION` is the maintainer's script and is Linux/Nix only. It builds `dist/i26-VERSION-windows.zip`, `-linux.zip`, `-linux-arm64.zip` (ETC2/ASTC textures for ARM GPUs) and `-macos.zip` (and, with `I26_ARMV7=1`, `dist/i26-server-VERSION-linux-armv7.zip`: the dedicated server alone, 32-bit ARM, no Godot; unverified on hardware). It uses Nix to get Godot's matching export templates (`.#export-templates`) and the .NET runtime packs from NuGet, so it needs network the first time. The macOS zip is a universal build that is ad-hoc signed by Godot only: it is not signed with an Apple developer ID, not notarised, and has not been tested on a Mac, so it is not an official platform.

## Subtitle generation (i26-subtitles)

`tools/I76.Subtitles` is a cross-platform CLI tool sharing the game's .NET runtime that transcribes cutscene videos (`.SMK`) and in-mission dialogue audio from the game install into time-aligned `.srt` and `.json` subtitle files using local Whisper AI (via Whisper.net).

```bash
dotnet build tools/I76.Subtitles/I76.Subtitles.csproj
dotnet run --project tools/I76.Subtitles -- --game all
```

It automatically downloads the required Whisper GGML model on first run into user cache, reads audio streams directly with zero external dependencies (no Python or FFmpeg required), and automatically synchronizes the generated files to your user data folder (`user://subtitles`), making them immediately active in the game. It compiles and runs on Windows, Linux, and macOS (with Apple Silicon Metal acceleration). See [docs/subtitles.md](docs/subtitles.md).

## Screenshots and profiling

`tools/shot.sh OUT.png --mission t01 --1997` renders one frame off-screen on Linux (see the header of the script for its options). It needs `I76_INSTALL`, a Godot .NET binary (`$GODOT`, default `godot-mono`, else `godot`), a built `game/` (`dotnet build game/I76.Game.csproj`) and either Weston or Xvfb. By default it uses a private headless Weston on the GPU; it falls back to Xvfb with Mesa's software Vulkan, which runs anywhere (force it with `I76_SHOT_GPU=0`; pick another GPU's Vulkan driver with `I76_SHOT_ICD`). `I76_SHOT_GL=1` renders with the OpenGL Compatibility renderer instead. It runs the game in a scratch user folder and never touches yours.

## Layout

```
src/I76.Formats/    readers for every container and asset format (no Godot)
src/I76.Sim/        the deterministic simulation (no Godot)
src/I76.Net/        the wire protocol and netcode
src/I76.Server/     the dedicated/listen server host
game/               the Godot 4 .NET project
tests/              xUnit
tools/              the `i76` CLI, `i26-subtitles` transcription tool, scripts for captures, assets and releases
docs/               the specifications the implementation follows (the behaviour of the original game) and the list of deliberate deviations
```

## Credits and licence

I26 is free software under the [GNU GPL v3](LICENSE) (`GPL-3.0-or-later`). Third-party components and assets are listed in `tools/release/THIRD-PARTY-NOTICES.txt`. The rules the project works by are in [CHARTER.md](CHARTER.md).

The map editor's object picker takes its labels from the **Complete Asset Bible for Interstate '76/Gold/Nitro** by DIVER, building on a text version by Skull N. Bones (1998) and an original document by Brian Dean Jennings: the editor shows its descriptions ("House (1-story ranch,
tan)") beside the object codes, and its notes decided which unused objects are left out. Thank you to all three.
