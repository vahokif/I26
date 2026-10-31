# Contributing to I26

Thanks for helping. [DEVELOPMENT.md](DEVELOPMENT.md) has the setup (.NET 8 SDK, Godot 4.7.x .NET, optional Nix), [AGENTS.md](AGENTS.md) the practical details (layout, commands, pitfalls) and [CHARTER.md](CHARTER.md) the rules. The short version:

- **Faithful gameplay first.** Physics, damage, AI and rules follow the original; a change that makes the
  game play differently needs evidence from the original. Rendering may be better than 1997.
- **Deliberate differences go in [docs/deviations.md](docs/deviations.md)**, one row each, with the option
  that switches it.
- **No game data in the repository.** No game files, extracted media, screenshots of game art, or text
  copied from the game. The game is read from the player's own install at runtime. Do not attach game
  files to issues or pull requests either.
- Keep comments brief and factual, in the style of the surrounding code.

## Build and test

```
dotnet build I76.sln && dotnet test I76.sln
dotnet build game/I76.Game.csproj
```

The Sim and Net tests need no game install. Tests that read game files need `I76_INSTALL=/path/to/Interstate76`
(they are skipped without it); the Nitro tests also need `I76_NITRO_INSTALL`. See [DEVELOPMENT.md](DEVELOPMENT.md).

Run the suite before sending a change, and check visual changes in both looks (modern and `--1997`).
Pull requests: one logical change each, with a description of how you checked it.
