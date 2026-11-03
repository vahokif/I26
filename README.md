<img width="550" height="36" alt="SOMEWHERE IN THE SOUTHWEST..." src="https://github.com/user-attachments/assets/fe88d0e6-128e-4a78-b573-138f98486eab" />

&nbsp;

This is a reimplementation of the classic game Interstate 76 in Godot engine.

[![I26 - Interstate '76 remastered (video)](https://img.youtube.com/vi/ph6Zrzbycx0/maxresdefault.jpg)](https://www.youtube.com/watch?v=ph6Zrzbycx0)

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/G4Q128JK2B)

# Features

* Modernized graphics
* 1997 mode - toggle the original graphics any time with F12
* Single player campaign, scenarios and auto melee
* Multiplayer with modern netcode
* Online coop mode for campaign and scenarios
* Easy to use 3D map editor
* 3D garage with respraying

## Optional
* Auto-reverse
* Mission checkpoints
* Botanically accurate vegetation
* Support for upscaled textures
* CB radio in multiplayer
* Car interiors
* Subtitles for cutscenes and in-mission dialogue

# Installation
Install the [GoG](https://www.gog.com/en/game/interstate76) version (and Nitro pack) from GoG Galaxy, and the game should find them automatically. If not, copy the Interstate 76 directory into the `i26` directory named `i76`, and/or the Nitro pack into the directory named `nitro`.

Alternatively, place the `setup_interstate_76_*.exe` offline backup installers into the `i26` directory and they will be unpacked automatically. If you're logged in to [gog.com](gog.com), the download links are: [Interstate 76](https://www.gog.com/downloads/interstate_76/en1installer1), [Nitro Pack](https://www.gog.com/downloads/interstate_76_nitro_pack/en1installer1)

On Linux, ensure Microsoft core fonts are installed for Impact and Arial.

### Subtitles (Optional)
I26 includes optional subtitle support for all 31 cutscenes and in-mission CB radio dialogue:
* **Installing Subtitles**: Place the `subtitles/` folder (`subtitles/i76` and `subtitles/nitro`) inside your `i26` game folder, or in your OS user data directory (`%APPDATA%\I26\subtitles\` on Windows, `~/.local/share/I26/subtitles/` on Linux, `~/Library/Application Support/I26/subtitles/` on macOS).
* **Generating Subtitles**: Run the included `i26-subtitles` tool (or `dotnet run --project tools/I76.Subtitles`) to transcribe cutscene and mission audio directly from your Interstate '76 game files using local Whisper AI.
* If no subtitles folder is present, the Subtitles option is automatically greyed out in the menus. See [docs/subtitles.md](docs/subtitles.md) for full instructions.

# Source code
I26 is free software under the [GNU GPL v3](LICENSE). To build it, run the tests or contribute, see [DEVELOPMENT.md](DEVELOPMENT.md); [docs/playing.md](docs/playing.md) has the details for players (how the game finds your install, settings and saves, controls, custom maps, system requirements and status).

I26 contains no game data. It is an unofficial fan project and it needs your own legal copy of Interstate 76.

# Credits
Thanks to the Interstate 76 community for testing, ideas, and supporting this project.