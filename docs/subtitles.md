# Subtitles

Subtitles are a modern port addition; the original 1997 Interstate '76 has none.
See the [deviations table](deviations.md) for the policy note.

---

## What is covered

| Kind | Source | Display |
|---|---|---|
| **Cutscene (`.smk`) subtitles** | One `.srt` file per movie, named by the movie stem (e.g. `int01f01.srt`, `introf01.srt`) | Overlaid dynamically on the movie player with exact word timing |
| **In-mission CB dialogue** | One `.json` file per mission (or shared `dialogue.json`), keyed by sound-file stem | Displayed on the HUD while the radio transmission plays |

Both **Interstate '76** (29 cutscenes, missions `t01`–`t17`, `s01`–`s08`, `a01`) and the **Nitro Pack** expansion (intro & credits cutscenes, missions `p01`–`p19`, `b01`) are fully supported.

---

## Installation Steps for Players

Players can install subtitles in either of two locations. Files are searched case-insensitively.

### Option 1: Game Directory (Portable)

Extract the `subtitles/` directory directly into your I26 game folder alongside the executable:

```
i26/
├── i26.exe (or i26.x86_64 on Linux / i26.app on macOS)
├── i26.pck
└── subtitles/
    ├── i76/
    │   ├── int01f01.srt
    │   ├── t01.json
    │   └── dialogue.json
    └── nitro/
        ├── introf01.srt
        ├── p01.json
        └── dialogue.json
```

### Option 2: User Data Directory (Per-User / System-Wide)

Alternatively, place them in your operating system's personal user data folder:

* **Windows**: `%APPDATA%\I26\subtitles\`  
  *(typically `C:\Users\<username>\AppData\Roaming\I26\subtitles\`)*
* **Linux**: `~/.local/share/I26/subtitles/`  
  *(or `$XDG_DATA_HOME/I26/subtitles/`)*
* **macOS**: `~/Library/Application Support/I26/subtitles/`

Inside this folder, organize files into `i76/` and `nitro/` subfolders as shown above.

---

## In-Game Controls & Settings

* Subtitles are enabled by default when subtitle files are installed.
* **Auto-Detection & Greyout**: If no `subtitles/` folder or subtitle files are found across the search paths, the Subtitles option is automatically greyed out, locked to "Off", and unclickable in both the main menu Audio Control screen and the in-game Escape menu.
* **Toggle in Main Menu**: Go to **Audio Control** in the shell options to toggle **Subtitles: On / Off**.
* **Toggle in Mission**: Press `Escape` while driving to open the in-game menu; toggle the **Subtitles** row under Audio.
* Setting persists across game sessions in `shell.cfg` under `[port] subtitles = 1`.

---

## How Files Are Discovered

`SubtitleLoader` resolves whether the active mission or movie belongs to Interstate '76 or Nitro Pack based on:
1. File prefix (`p##`, `b01` for Nitro; `t##`, `s##`, `m##`, `a01` for I76)
2. Active game profile (`SubtitleLoader.IsNitro`)
3. Path components (`/nitro/` vs `/i76/`)

It searches locations in the following priority order:
1. Registered runtime search directories (`SubtitleLoader.RegisterSearchDirectory`, including `user://subtitles` registered by `Main.cs` on boot).
2. The OS user data path (`%APPDATA%\I26\subtitles` on Windows, `~/.local/share/I26/subtitles` on Linux, `~/Library/Application Support/I26/subtitles` on macOS).
3. The executable directory (`subtitles/i76` and `subtitles/nitro`).
4. The active game install directory (`subtitles/` or root).
5. Current directory `subtitles/`.

---

## Generating Subtitles (`i26-subtitles`)

The native subtitle generation tool lives in `tools/I76.Subtitles` and is compiled with the game's solution. In release packages, `i26-subtitles` (or `i26-subtitles.exe` on Windows) is included alongside `i26` and `i26-server`, sharing the game's `.NET 8` runtime.

### Features
* **Zero External Dependencies**: Reads Smacker (`.SMK`) video/audio streams and `.ZFS` archives directly in C# using `I76.Formats`. No Python or FFmpeg required.
* **Automatic Model Download**: Downloads the required Whisper GGML model on first run into user cache (`%APPDATA%\I26\cache\models` on Windows, `~/.local/share/I26/cache/models` on Linux).
* **Automatic User Folder Sync**: Automatically writes output to the output folder AND copies directly into the user's personal subtitle directory (`user://subtitles`), making them immediately active in-game.
* **Cross-Platform**: Runs on Windows, Linux, and macOS (with Apple Silicon Metal acceleration).

### Usage
Run from the repository root:
```bash
# Transcribe everything (both I76 and Nitro):
dotnet run --project tools/I76.Subtitles

# Or run the published/release binary directly:
./i26-subtitles

# Specific options:
dotnet run --project tools/I76.Subtitles -- --game i76 --cutscenes-only
dotnet run --project tools/I76.Subtitles -- --game nitro --model small.en
dotnet run --project tools/I76.Subtitles -- --filter int08
```

### CLI Options
| Flag | Description | Default |
|---|---|---|
| `--game <all\|i76\|nitro>` | Game profile to process | `all` |
| `--i76-install <dir>` | Path to Interstate '76 files | Auto-detected / `$I76_INSTALL` |
| `--nitro-install <dir>` | Path to Nitro Pack files | Auto-detected / `$I76_NITRO_INSTALL` |
| `--out <dir>` | Root output folder | `subtitles/` |
| `--model <name>` | Whisper GGML model (`tiny.en`, `base.en`, `small.en`) | `base.en` |
| `--model-path <path>` | Path to custom `.bin` model file | Auto-download |
| `--filter <pattern>` | Filter cutscenes or missions by pattern | All |
| `--threads <n>` | CPU threads for Whisper inference | Half CPU cores |
| `--cutscenes-only` | Transcribe only cutscenes (`.SMK`) | Both |
| `--dialogue-only` | Transcribe only in-mission dialogues (`.WAV`) | Both |
| `--skip-existing` | Skip files that already have subtitle output | False |
| `--no-copy-user` | Disable syncing to user data directory | False |

> [!IMPORTANT]
> In accordance with the project's [Clean-Room Charter](../CHARTER.md) and [Contributing Guidelines](../CONTRIBUTING.md), raw `.srt` and `.json` transcription files are derived from game audio and **must never be committed to Git**. They live only in player user-data directories, distribution release packages, or local builds.

---

## File Formats

### `.srt` — Cutscenes
Standard SubRip format, UTF-8 encoded with millisecond timing:
```srt
1
00:00:41,900 --> 00:00:43,500
Root beer.

2
00:00:44,260 --> 00:00:45,500
Thanks.
```

### `.json` — In-Mission Dialogue
JSON object mapping sound stems to character-attributed dialogue strings:
```json
{
  "01sta09": "Taurus: Okay baby, here's the scoop...",
  "01sgc06": "Groove: Yeah, I got it.",
  "02dri08": "Drinky: You can't kill me! I'm Drinky, the stunt-driving clown!"
}
```

---

## Dynamic Text Paging

Lengthy radio dialogue lines (such as Taurus's poems) are automatically divided into readable pages of ~100 characters via `SubtitleTrack.Page(text, elapsed, duration)`. As the sound plays, the subtitle smoothly advances from one stanza/chunk to the next without truncating text or overflowing the screen.

---

## C# API Reference (`src/I76.Formats/Subtitles/`)

### `SubtitleLoader`
```csharp
// Register search directory at startup:
SubtitleLoader.RegisterSearchDirectory(path);

// Set game mode (automatically done in Main.cs):
SubtitleLoader.IsNitro = true;

// Load subtitle track by movie or mission stem:
SubtitleTrack? movieTrack   = SubtitleLoader.Load("int01f01"); // loads .srt
SubtitleTrack? missionTrack = SubtitleLoader.Load("t01");      // loads .json
```

### `SubtitleTrack`
```csharp
// Lookup text for movie player by playback timestamp in seconds:
string? text = movieTrack.AtTime(currentSeconds);

// Lookup dialogue text by audio sound stem:
string? text = missionTrack.BySound("01sta09");

// Page lengthy text over duration:
string pagedText = SubtitleTrack.Page(fullText, elapsedSeconds, totalDuration);
```
