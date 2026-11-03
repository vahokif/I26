# Documentation index

Start with [architecture.md](architecture.md) for how the code is laid out, then
[deviations.md](deviations.md) for every place where this game deliberately differs from the
original. Working on the code: see [../AGENTS.md](../AGENTS.md) and [../CONTRIBUTING.md](../CONTRIBUTING.md);
the rules of the project are in [../CHARTER.md](../CHARTER.md).

## Overview

| Doc | What it is |
|---|---|
| [architecture.md](architecture.md) | Projects, coordinates, time, randomness. |
| [deviations.md](deviations.md) | Intentional differences from the original, with the option that switches each. |
| [dev-hooks.md](dev-hooks.md) | Every `I76_*` environment variable: capture, test and debug hooks. |
| [map-editor.md](map-editor.md) | The in-game map editor: terrain brushes, undo, playtest, saving to `addon\`. |
| [map-format-writing.md](map-format-writing.md) | Writing missions and terrain back in the original formats. |

## Behaviour notes

| Doc | What it is |
|---|---|
| [frame-rate.md](frame-rate.md) | The original's frame-coupled rules and the Simulation Rate setting. |
| [modern-physics.md](modern-physics.md) | The optional Modern Physics driving model. |
| [multiplayer.md](multiplayer.md) | The network melee: protocol, snapshots, prediction. |
| [savestate.md](savestate.md) | Snapshots of the whole simulation: what they hold, versioning, the completeness guard, restoring. |
| [modern-vegetation.md](modern-vegetation.md) | The modern look's procedural plants: species and their L-systems, placement, drawing, cost. |
| [compatibility-renderer.md](compatibility-renderer.md) | Running on OpenGL when there is no Vulkan or D3D12: what is lacking and what the game does. |
| [subtitles.md](subtitles.md) | Cutscene and mission subtitles: speech-to-text generation, SRT and JSON formats, player installation and options. |
