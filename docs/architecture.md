# Architecture

```
          install (read-only)
                │
        I76.Formats ── Vfs: ZIX loose files > ZFS records > pak members
                │       Bwd2, Geo, Imaging, Mission, Fsm
        ┌───────┴────────┐
     I76.Sim          game/ (Godot)
  deterministic       MeshBuilder, TextureFactory, MaterialLibrary
  StepFrame(Δ, in)    MissionBuilder, TerrainNode, ScenerySpawner
        │             cameras, UI, audio
        └──── state ──────▶ render (interpolated)
```

- `I76.Formats` and `I76.Sim` are plain net8.0 libraries with no Godot
  reference, so the tests and the CLI run without an
  engine.
- **No install, an installer.** `I76.Formats.Installers` reads GOG's offline
  installers (Inno Setup 5.0 and later: the loader's offset table, the
  versioned headers, LZMA/LZMA2/zlib chunks across `setup-N.bin` slices,
  the x86 call filter, checksums) and extracts the game data into an
  ordinary install folder (`i76/`, `nitro/` inside the I26 folder, else in the local user folder); the game's
  no-install screen (`Boot/InstallerScreen.cs`) and `--from-installer` drive it.
- **Coordinates.** The game is left-handed, and Godot is right-handed. `Coords` in
  the game project mirrors Z and reverses triangle winding at the boundary.
  The simulation keeps the game's own frame. Worlds are about 51 km across,
  so the renderer uses a floating origin.
- **Time.** `SimWorld.StepFrame(frameDelta, controls)` does what one
  original frame does, in the original's order: the clock clamp, substeps,
  controllers, integrators. Play mode steps at a fixed tick (the
  Simulation Rate setting, default 20 Hz = 102/2048 s; `SimRate`) and
  interpolates for drawing. Reference mode replays the frame deltas from a
  recorded trace. `SimSession.Tick(inputs)` is that step for play: one
  `CarInput` per human-driven car, everything else from inside the sim
  (docs/multiplayer.md); `SimDriver` is its Godot host.
- **Randomness.** One `CrtRand` (the MSVC LCG) stream is shared the way the
  original shares `rand()`. In oracle mode it replays the values from a trace.
