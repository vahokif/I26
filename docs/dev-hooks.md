# Environment variables (development hooks)

Every `I76_*` variable the code and the scripts read. None of them is needed to play. They exist for
off-screen captures (`tools/shot.sh`), tests, profiling and debugging; unset, the game behaves normally.
Kinds: **capture** (put the game in a known state for a screenshot), **test** (drive a scripted run),
**debug** (extra output or switches), **tuning** (A/B a render or sim choice), **setup** (where things are).

## Setup

| Variable | Kind | What it does |
|---|---|---|
| `I76_INSTALL` | setup | Directory of the game install (holds `I76.ZFS` or `nitro.zfs`). Read by the game, the CLI, the tests and the scripts; `--install DIR` wins over it. |
| `I76_NITRO_INSTALL` | test | A Nitro Pack install; the Nitro tests are skipped without it (`I76_NITRO` is accepted by the Net tests as an alias). |
| `I76_GOG_INSTALLER`, `I76_GOG_NITRO_INSTALLER` | test | A GOG offline installer of Interstate '76 / the Nitro Pack (`setup_....exe`). `InstallerIntegrationTests` parse and extract it into `~/.cache/i26-tests/gog-game*` (several hundred MB, replaced each run), compare every file with `I76_INSTALL` / `I76_NITRO_INSTALL` when set, and run `t01` / `p01` headless on the result; skipped without it. Tests only: players pick the installer in the game. |
| `I76_SWITCH_TEST=DIR` | capture | The in-process "Switch Game" guard (`tools/switch-test.sh`, `Boot/SwitchProbe.cs`): drives chooser, base menu and mission, switch, Nitro menu and mission, switch, base menu; checks install and statics, saves captures into DIR, prints `switch-test: PASS`. |
| `I76_SHOW_SWITCH=1` | capture | Offers Options' "Switch Game" row as if the game chooser had been shown (for a capture of it). `--choose-game` forces the chooser. `I76_INSTALL=none` with a scratch `I76_SHOT_DATA` holding `I26/game` (an install or a symlink to one), `I26/game-nitro` and/or `setup_*.exe` symlinks captures the chooser (`GameChooser`). |
| `I76_GOG_GALAXY_STUB` | test | A GOG Galaxy downloader `.exe`, which `InstallerIntegrationTests` expect to be refused as one. |
| `I76_CUSTOM_MAPS_I76` | test | Base Interstate '76 community maps unzipped under one folder (each map's `.lvl` and `.ter`, a folder per map or not); the base-map tests put them in scratch base and Nitro installs and skip without it and `I76_INSTALL`. Tests only. |
| `I76_CUSTOM_MAPS` | test | The unzipped community Nitro map pack (the folder holding its `miss16`, `miss8` and `addon`); `CustomMapTests` build a scratch install of links from it and `I76_NITRO_INSTALL` and skip without both. Tests only: the game reads custom maps from the install's own folders. |
| `I76_TEX_DIR` | setup | A folder of replacement menu art, read in place of the original's (development). |
| `I76_SND_DIR` | setup | A folder of HiFi Audio (replacement sound samples and movie sound tracks), read in place of the original's (development; otherwise `user://snd/`, or the newest `i76-hifi-audio*.zip`, or the first release's `i76-hd-sounds*.zip`, beside the executable or in `user://`, read in place). Layout (engine 1.1): `pack.json` (`name`, `version`, `engine`, `count`); `sounds/NAME.ogg`, NAME the sound's file name as the game asks for it, lower case, without extension (the front end's database sounds are `dbN`, N the base game's record), the original's length (a different one is logged and used): only the samples change, loop mode, priority, voice limits, pitch and volume stay the original's; `movies/NAME.ogg`, NAME the Smacker movie's file name, lower case, without extension (`INT08F01.SMK`: `movies/int08f01.ogg`): the movie's whole sound track in place of its own (track 0, the only one the games' movies have: 22050 Hz 8-bit mono, 16-bit stereo in the Nitro Pack's intro), 48 kHz, its own track's sample count × 48000 / 22050 rounded (the picture's length to within a frame); it starts with the first frame and the picture follows it as heard; skipping stops it; the end logs the sync. `sounds/i76/`, `sounds/nitro/`, `movies/i76/` and `movies/nitro/` win over the shared folder in their game (both games have a `CREDF01` and an `INTROF01`). Ogg Vorbis, mono or stereo. Audio Control's HiFi Audio row (the front end's and the mission menu's), shown only then, switches it; stored as `[port] hd_sounds`. |
| `I76_SND_TRACE` | debug | `1`: a line for every sound such a folder or pack supplies, as it is first loaded (with the running count, size and load time). A movie's track is logged whether or not. |
| `I76_NO_SYSTEM_FONTS` | debug | `1`: Impact and Arial from the replacement menu art's folder only, never the system's. |
| `I76_FONTS_MISSING` | test | `1`/`both`, `Impact` or `Arial`: the shell's one-time "fonts not installed" notice behaves as if those faces were missing, every start and with no marker written; `0`: none missing. |
| `I76_GROUND_RES` | setup | `tools/fetch-ground.sh`: `2K` fetches the 2K ground sets (1K by default). |

## Capture scripts (`tools/shot.sh`, `tools/frame-profile.sh`, `tools/lan-test.sh`)

| Variable | Kind | What it does |
|---|---|---|
| `I76_SHOT_SIZE` | capture | Render size, `WxH` (default 1280x720). |
| `I76_SHOT_FPS` | capture | Step the game at a fixed frame rate (`--fixed-fps`). |
| `I76_SHOT_MAX_FPS` | capture | At most that many frames a real second (paced by `FrameLimiter.Pace`: Godot's `--max-fps` does nothing under `--fixed-fps`). With `I76_SHOT_FPS` the same the capture runs in real time, which a network game needs: its listen server ticks and its bots play on the wall clock, so a slower capture shows them sped up. |
| `I76_NET_TIME_SCALE` | capture | `S`: the network game's clock (`NetClock`) runs at S times the wall clock: the listen server's ticks, the client's view of them, and `i26-server --bots` given the same value. For a network capture the machine can't render in real time: `I76_SHOT_FPS=30 I76_SHOT_MAX_FPS=15` with S 0.5 in both processes records a game at half speed that plays back at full. |
| `I76_SHOT_FRAMES` | capture | Quit after N frames (default 12); the shot is frame N-2. |
| `I76_SHOT_SERIES` | capture | Also save the last K frames. |
| `I76_SHOT_EVERY` | capture | Save every Nth sim tick as `OUT_<frame>.png`. |
| `I76_SHOT_PIPE` | capture | `PATH`: the `I76_SHOT_SERIES` frames go there as raw RGB24 at the render size, one after another, instead of PNGs (a FIFO an encoder reads: `ffmpeg -f rawvideo -pix_fmt rgb24 -s WxH -r FPS -i PATH`), so no PNG is compressed on the main thread. The final screenshot is still a PNG. |
| `I76_SHOT_MOVIES` | capture | `1`: play the intro and briefing movies (skipped otherwise). |
| `I76_SHOT_GPU` | capture | `0`: software rendering (Xvfb + lavapipe) instead of a private headless Weston on the GPU. |
| `I76_SHOT_ICD`, `I76_SHOT_ICD_DIR` | capture | The GPU's Vulkan ICD json, or the folder searched for the default (radeon). |
| `I76_SHOT_DISPLAY` | capture | X display number for the software path. |
| `I76_INSTALL=none` | capture | `tools/shot.sh` leaves `--install` out: the no-install screen (the game no longer sees the value; with a fresh `I76_SHOT_DATA`, so nothing is found in the user folder). With `--from-installer`, the extraction screen's shot goes to `OUT_installer.png`. |
| `I76_SHOT_DATA` | capture | Scratch `user://` root to use (a prepared state). Default: one per slot under the cache folder. |
| `I76_SHOT_LIVE_DATA` | capture | `1`: use the real user data folder (the default is an isolated one; this is rarely what you want). |
| `I76_LAN_ARGS`, `I76_LAN_FRAMES`, `I76_LAN_NET_TEST`, `I76_LAN_NAV`, `I76_LAN_HOST_IN`, `I76_LAN_JOIN_IN` | test | `tools/lan-test.sh`: extra game args, frame count, the `I76_NET_TEST` value, and replacement click scripts for the menus. |
| `I76_ANET_ARGS`, `I76_ANET_NAV`, `I76_ANET_HOST_IN`, `I76_ANET_ROSTER_IN`, `I76_ANET_JOIN_IN` | test | `tools/anet-test.sh`: extra game args and replacement click scripts (the Nitro Pack's menu sits elsewhere). |

## In-game capture hooks

| Variable | Kind | What it does |
|---|---|---|
| `I76_PAD_AXES` | capture | `lx,ly,rx,ry,lt,rt` (stick axes -1..1, triggers 0..1): the Pad Dead Zones screen shows these in place of a pad, so its gauges can be captured without one (docs/joystick.md). |
| `I76_PAD_FOCUS` | capture | `1`: the pads read as if the window had focus (a capture compositor has no seat, so the window never does). With a virtual joystick made through `/dev/uinput`, this is how the joystick screen is captured; see docs/joystick.md. |
| `I76_GAME_KEYS` | capture | Scripted key presses in a mission, `"escape@40;down@44;enter@48"` or `kp8@20-300` (held). Runs while the tree is paused too. |
| `I76_MOUSE` | capture | Scripted mouse motion in a mission, `"60@300,0;80@0,-40"` (frame@dx,dy): the Mouse Camera orbit, with `--drive`; `"40@w2;80@w-3"` turns the wheel (positive up: nearer) for the external view on screen (F2/F3/F9 chase, F4/F5, F6, F7/F8, F10, F11; not the cockpit); `"320@c571,619"` clicks the left button at that window pixel (pressed that frame, released the next; runs while the tree is paused, for the Developer panel's buttons). |
| `I76_CURSOR_LOG` | test | Logs each change of the mouse mode (`[cursor] Captured -> Visible`), from the one function that sets it (`Main.ApplyCursor`, state from `CursorPolicy`). With `I76_MOUSE` set the window counts as focused. |
| `I76_SHELL_INPUT` | capture | Scripted mouse and keys for the front end, `"move:x,y@frame;click:x,y@frame;key:escape@frame"`, in shell coordinates. Also `keyev:Semicolon,58,shift@frame` (a key press with its Unicode and modifiers, for the text fields; the log says what was typed) and `launch:trip,t16,CAR.vcf@frame` (a mission handed to the game as the forms would) and `hold:x,y@frame` / `release:@frame` (press and keep the left button down at x,y: for the arrows that repeat while held) and `overlay:controls@frame` `overlay:mouse@frame` `overlay:graphics@frame`, `overlay:play@frame` or `overlay:audio@frame` (the Control Configuration / Mouse Options / Graphic Detail / Play Options / Audio Control over the screen) and `trip:t03,CAR.vcf@frame` (that trip mission started as NEW TRIP does, the car the trip's, so a win comes back through the router, FIELD SALVAGE and the garage). |
| `I76_SHELL_START` | test | `melee:N`: open that melee form (3 is the LAN host form). `router`: open the shell as if the trip's current mission had just been won. `standings:combat\|race\|ctf[:team]`: STANDINGS over made-up rows. |
| `I76_SHELL_THROW` | test | A screen's class (e.g. `FieldSalvageScreen`) or `FirstScreen`: the shell fails there, to see its recovery (the main menu with a notice, the exception in the log). A pop-up's class (`AddressBookPopup`, `SessionPopup`): that pop-up fails opening, closes, and a notice shows over the screen under it. |
| `I76_MENU_PICK` | capture | Pick that mission (e.g. `A01`) from the main menu on the third frame. |
| `I76_MENU_SHOW` | capture | Select that mission in the F9 list (e.g. `M15`) and scroll to it, without playing it. |
| Ctrl+F8 | debug | In a mission and in the 3D garage: the cars' glass off and on, to compare. |
| `I76_CAR_INTERIORS` | capture | `0`: Graphic Detail's Car Interiors off for the run (the windows plain reflective glass, no cabin or driver behind them). `I76_CAR_MASKS=0` leaves the segmentation maps out (no windows, the all-over clear coat but the table's hand rows), `I76_CAR_MASKS_FILE=PATH` reads another `i76 car-project` result; `I76_CAR_WINDOWS_LOG=1` prints each car's cabin box and glass texel counts, `I76_CAR_NORMALS=geo` keeps the GEOs' normals on the cars whose normals are estimated, `I76_CAR_NORMALS_VIEW=1` draws car normals as colour. |
| `I76_WINDOWS_STEER` | capture | `s`: the steering the car-window driver's wheel and hand show (-1..+1, +1 full left) in place of the car's own, for captures. |
| `I76_CAP_CAM` | capture | `dx,dy,dz,yaw,pitch`: a fixed camera at that offset from the player's car (Godot axes, degrees). |
| `I76_CAP_CAM_ON` | capture | `ODEF`: with `I76_CAP_CAM`, the offset is from the first car whose definition starts with this (`vcpolic`) instead of the player's. |
| `I76_FREECAM` | capture | `x,y,z,yaw,pitch[,fov]`: hold the fly camera there during a mission (fov: vertical, degrees; the maintainers' tooling logs the original's camera in this form). |
| `I76_CHASE_YAW`, `I76_CHASE_ZOOM` | capture | Start the chase camera swung round the car (degrees) or nearer or further (1..10). |
| `I76_HEAD`, `I76_HEAD_STATE` | capture | Cockpit head look `yaw,pitch`, or hold a glance state 0..7. |
| `I76_VEG_GALLERY` | capture | `x,z[,species,...]`: the mission's species (or the ones named, from any region, e.g. `creosote,ocotillo`; `all`: every one) in a row along game X from (x, z), 4.5 m apart, their variants in rows behind; nothing else grows there. T16's flat: `1652,50137` (`I76_FREECAM=1657.5,10,50128.5,0,-10,50` after the intro). `I76_VEG_GALLERY_STEP=1.4` packs small plants closer; `I76_VEG_GALLERY_TIER=1` or `2` shows the middle or far meshes. |
| `I76_TUMBLEWEEDS` | capture | `N`: that many rolling tumbleweeds round the camera (16 at most) whatever the biome and Vegetation's level say, wherever there are plants (`0`: none; Vegetation Off and the 1997 look still have none) (docs/modern-vegetation.md, Tumbleweeds). |
| `I76_TUMBLEWEED_AHEAD` | capture | `D[,S]`: put the first tumbleweed D metres ahead and S to the right of the player's car as it drives off (of the camera with no car moving, after 3 s), a second or more into the mission: one to drive into (T01 `--drive --chase` with `12,-1` and `I76_CAP_CAM=0,2.5,8,0,-8` bursts it at about frame 55 at 30 fps) or to watch roll by. |
| `I76_CAB_CLIP` | capture | `0`: no cab clip and no plant bend around cars (for before and after captures). |
| `I76_VCF_SWAP` | capture | `car.vcf:old=new,old=new`: that car with other weapons in its file (names without `.gdf`, `vppirna1.vcf:gfmedium=gwhiteph` puts the WP Mortar where the flame thrower was), for a capture that needs a weapon the stock cars lack; with `--drive car.vcf`. |
| `I76_SIREN_LIGHTS` | tuning | How many police light bars (the nearest to the camera) cast red and blue light at night in the modern look; 4 by default, 0 for none, -1 leaves the whole modern bar out. |
| `I76_HEADLIGHTS` | tuning | How many other cars' headlamps (the nearest to the camera) cast real light at night in the modern look; 6 by default, 0 for none. The player's always do. |
| `I76_SELF_LIT` | tuning | `0`: the modern look's night leaves the structures' self-lit faces unlit and their lamps out (the Paradise gas station dark, as before they were added); `noshadow`: the lamps cast no shadows. |
| `I76_RETRO_SIGNS` | tuning | `0`: the modern look's night leaves the road signs' faces ordinary, not retroreflective (lit by the headlamps' diffuse light alone, as before), for before and after captures. |
| `I76_FPS_LOG` | test | `1`: every two seconds print `fps-log:` (frames a second, sim ticks a second and the tick, frame time mean, deviation and worst): the Max FPS cap against the sim rate. |
| `I76_HOUR` | capture | Override the mission's hour of day for the lighting. |
| `I76_CAR_CONDITION` | capture | `N`: every car shows damage level N. |
| `I76_CAR_DAMAGE` | capture | `L` (0..3): every car's zones shown at damage level L, shape and textures, as a hit leaves them (DamageView). |
| `I76_SMOKE` | capture | `h` (0..1) holds every living car's health at h; `0` leaves the damage smoke out. |
| `I76_SMOKE_RATE` | capture | `k` scales the modern damage smoke's emission (3 for a thick plume to see through). |
| `I76_SMOKE_LIGHT` | tuning | `k` scales how strongly the lamps light the modern smoke (0.2 by default; 0 for none). |
| `I76_SMOKE_CAP` | tuning | `c` (0..1): the most opaque the modern smoke layer gets (0.65 by default; SmokeLayer). `I76_SMOKE_LAYER=0` leaves the layer out (every smoke draws itself, each puff capped); `I76_SMOKE_SCALE=N` draws it at 1/N size (2). `I76_SMOKE_DEPTH_VIEW=1` draws each puff of the layer as a square showing the scene's depth as the layer reads it, a cycle of hues for each doubling of the distance and red where the scene is nearer than the puff: the outlines of the cars and hills should show in the squares; a flat square means the layer gets no depth on that GPU or backend. |
| `I76_WRECK_FIRE` | tuning | `0`: no wreck fire (nor the burning scenery wrecks' StaticFire); `heat:H` holds the heat at H (0..1). |
| `I76_BOOM` | capture | `tick[,effect]`: an explosion 15 m ahead of the player at that tick. Effects: `kill-node:NAME:PART` (the nearest live structure NAME's nodes whose name contains PART destroyed as a round would, a bridge's rails with `RL`), `kill-struct:NAME` (the live structure of that name nearest the player, destroyed whole as a script's destroy does, and logged as `[boom] destroyed ...`), `kill`, `kill-ai` (the nearest other car), `kill-all` (every other car), `hit-ai[:N]`, `hit-me[:N[:zone]]`, `kill-gun`, `kill-struct` (the live structure nearest that point, destroyed as the script's `destroy` does). Each entry prints a `[boom] tick N: effect` line as it fires, to place it among the `hitch:` lines. |
| `I76_SPAWN_AT` | capture | `x,z,yaw[;x,z,yaw@s...]`: put the player's car there, at rest; a stop with `@s` waits for mission second s (a script's own teleport at the start overrides a stop without one: T07). |
| `I76_SPAWN_AI_AT` | capture | `right,ahead[,yaw[,speed]][;right,ahead...][@frame[-until]]`: move the nearest other car there, in the player's frame at that tick; several places move the nearest cars, one each. Held up to `until`, a car stays where it was first put, so a moving player drives past it. |
| `I76_AUDIO_LOG` | capture | `1`: print `[audio]` lines for the starts and stops of the horn, wind, sirens, honks and ramp thumps, the 600 m cull, and the slider previews (audio is silent in a capture). |
| `I76_AUDIO_REC` | capture | `file.f32`: the Master bus's left channel recorded in a mission as little-endian float32 (the mix rate, 44100 Hz), for looking for clicks in a loop; everything audible is in it (engine, wind), so hold only the sound under test. `I76_AUDIO_REC_STEREO=1` writes left and right interleaved, for checking a sound's panning. |
| `I76_MELEE_OPPONENTS` | capture | `a.vcf,b.vcf`: opponents for `--melee`. |
| `I76_HOLD_HARDPOINT` | capture | `N` (1-5): keep hardpoint key N down. |
| `I76_HOLD_BRAKE`, `I76_AUTO_TARGET` | capture | Hold the brake; target automatically. |
| `I76_HOLD_STEER` | capture | `left` or `right`: keep that steering key down. |
| `I76_GARAGE_DEBUG` | debug | `1`: every 20 frames the 3D garage prints its viewport, camera and car (`garage3d: container ...`). |
| `I76_GARAGE_CAM` | capture | `x,y,z,tx,ty,tz`: the 3D garage's camera held at that world point, looking at the other (the bay is 16 m wide, 18 deep; the turntable's middle is the origin, the car faces -Z). |
| `I76_GARAGE_TOUR` | capture | `DIR[;FIRST;LAST]`: the 3D garage photographs every model (FIRST..LAST, 0-based) and every variant from four angles into DIR (an index.txt beside), then quits; `I76_GARAGE_TOUR_ONLY=N` just variant N, `I76_GARAGE_TOUR_EXTRA=1` the paint schemes no variant wears, `I76_GARAGE_TOUR_CAMS=x,y,z,tx,ty,tz;...` fixed cameras instead. Run it with `I76_SHELL_START=garage:0:1` and `I76_SHOT_FRAMES` high enough. |
| `I76_CAR_TABLE` | capture | `0`: the modern look ignores the cars' window data (no glass: the paint all over), for before and after captures. |
| `I76_GARAGE_FOCUS` | capture | `overview`, `hp:N`, `wheel:front\|rear`, `side:N`, `engine`, `specials`, `under`: the 3D garage's camera held on that view whatever page is open (`GarageViews`); with `I76_SHELL_START=garage:MODEL:VARIANT` and `I76_SHOT_FRAMES` about 120. |
| `I76_GARAGE_PAINT` | capture | `PRIMARY[,SECONDARY][,gloss\|metallic\|matte]`: the 3D garage's car in these respray colours (hex, `-` for none); `I76_GARAGE_PAGE=respray` opens the RESPRAY page. With `I76_SHELL_START=garage:MODEL:VARIANT` (0-based model, 1-based variant) the garage opens straight on that car. |
| `I76_ADDON_CARS` | capture | `a.vcf,b.vcf`: car files (paths) put in the addon overlay under their names, a saved variant for `--drive`. |
| `I76_UNLIM_TURRETS` | capture | `1`: Unlim. Turrets on, whatever the shell.cfg says. |
| `I76_GARAGE_TURRETS` | capture | `1`: the 3D garage's car comes with a turret fitted on every roof mount (with `I76_UNLIM_TURRETS=1`, on both). |
| `I76_GARAGE_2D` | capture | `1`: CONFIGURE CHASSIS opens as the original's 2D form whatever Graphic Detail's 3D Garage says (for a side-by-side capture). |
| `I76_GARAGE_WDF` | capture | `wauto_3a.wdf`: the 3D garage's car comes on that wheel file (front and rear), to look at a wheel style. |
| `I76_GARAGE_MUSIC` | capture | `DIR[;DIR]`: the installs whose soundtrack the 3D garage's boombox plays, in place of the running game and the other game found on the machine. |
| `I76_GARAGE_MUSIC_SEEK` | capture | `S`: the boombox starts each track S seconds in (with `I76_AUDIO_LOG=1` the bus's peak is printed, `[audio] garage radio:`; a capture's silent Dummy driver still mixes). |
| `I76_EDITOR_MUSIC_SEEK` | capture | `S`: the map editor's music starts each track S seconds in (to hear a track end and the next begin). |
| `I76_NET_HUD_DEMO` | capture | `players[,what...]`: the network HUD filled with made-up players and lines in a network game (`--host-listen`), for captures of each state: `board`, `teams` (team play with the standings), `hint` (the standings key's reminder), `longchat` (long lines, an unbroken word and a long talk: wrapping and history; with `typing` a long line being typed), `teamplay`, `ctf`, `race`, `chat`, `typing`, `dead`, `ready`, `end-win`, `end-lose`, `timeup`, `final`, `invite`, `boot`, `host`, `client`, `page2`, `pad`, `cb` (the CB panel: you transmitting, two others on the air), `me:NAME` (your own row's name) (`NetHudDemo`). |
| `I76_CB_INPUT`, `I76_CB_AT`, `I76_CB_RECORD`, `I76_CB_QUIT`, `I76_CB_SHOT`, `I76_CB_PRESS` | test | `I76_CB_PRESS=1` holds the talk key for 12 s starting `I76_CB_AT` seconds in (to see what a press opens: with CB Microphone on None only the "choose a CB Microphone" notice, else the audio input, in the log). `I76_CB_SHOT=out.png` saves the view 1.5 s into this player's transmission (the raised handset, the CB HUD rows). The CB voice chat (docs/multiplayer.md "CB voice"): `I76_CB_INPUT=file.wav` is the microphone (a PCM WAV; the game talks `I76_CB_AT` seconds, 4 by default, after the scoreboard lists two players, once through, in real time); `I76_CB_RECORD=out.wav` writes what the radio plays this game (the other players' voices after the radio effect, at the mix rate, mono 16-bit; written at each talk's end and on exit); `I76_CB_QUIT=1` ends the game 8 s after the input is sent, or 2 s after a talk of 2 s or more has been recorded. `tools/cb-test.sh OUTDIR` runs a listen host and a client with them. |
| `I76_NET_HURT` | capture | `N`, on a listen server: damage the nearest other car. |
| `I76_NET_BOT` | capture | `1`: in a network game the own car drives itself as `i26-server --bots`' hunters do (`BotDriver`): makes for the nearest car and fires at it, and comes back after dying; in a co-op mission it plays as a teammate (hunts only the enemy team, keeps up with the lead, the lead with Taurus). For footage with nobody at the wheel. |
| `I76_LOADING_CARD` | capture | Show that mission's loading card (e.g. `b01`). |
| `I76_LOADING_SHOT` | capture | `PATH`: save the loading screen's frames. |
| `I76_LOADING_ANSWER` | capture | `N`: answer the loading screen's question (a host's custom map: 0 Download, 1 Always, 2 Cancel) with that button once it has been up 60 frames. |
| `I76_PIT_DUMP` | capture | `DIR`: dump the cockpit panel textures once, at frame 15. |
| `I76_NO_OVERLAY` | capture | Leave the debug overlay out. |
| `I76_NO_SCRIPT_CAM` | capture | `1`: ignore the mission script's camera moves (keep the cockpit view). |
| `I76_MOVIE_HOLD` | capture | Hold a movie on a frame number. |
| `I76_MOVIE_SKIP` | capture | `N`: skip a movie N drawn frames after its first, as a key would (with Load During Briefing a press before the mission is ready is kept, "PLEASE STAND BY" faded in, and done when it is). |
| `I76_MOVIE_END` | capture | `N`: the movie runs out N drawn frames after its first, as at its end (with Load During Briefing, its last frame held under "PLEASE STAND BY" until the mission is ready). |

## Map editor (docs/map-editor.md)

| Variable | Kind | What it does |
|---|---|---|
| `I76_EDITOR=MAP` | capture | Start in the map editor on that map (a stock mission such as `t01`, or a map in `addon\`), as the shell's Map Editor entry does. Quitting the editor ends the program. |
| `I76_EDITOR_CAM` | capture | `x,z,yaw,pitch,distance`: the editor camera looking at that ground point (game metres, degrees; pitch negative looks down). |
| `I76_EDITOR_SCRIPT` | capture | `frame:command:args;...` run by the editor on its own frame count (a scene reload starts it again). Commands: `stroke:tool,x,z,radius,strength,seconds[,falloff[,height[,vx,vz]]]` (one stroke of raise, lower, smooth, flatten, level or noise at game x, z held for `seconds` at 30 steps a second, the cursor drifting at vx, vz metres a second), `cursor:x,z` (hold the cursor there: an off-screen window has no mouse), `tool:NAME` (also `Surface`), `surface:ID[,overlay 0|1]` (the Surface tool's type, 0-7; `stroke:Surface,x,z,radius,1,seconds` then paints it), `brush:tool,radius,strength,falloff[,height]` (set the brush without painting), `height:x,z` (logs the edited ground there, the document's modified flag, revision and undo state), `undo`, `redo`, `look`, `music` (the MUSIC button), `key:NAME` (a key pressed through the window, e.g. `key:F12`), `playtest[:bar]` (as F5, or with `:bar` as the PLAYTEST button; the car goes under the cursor held by `cursor:x,z`, else at the camera's pivot), `help`, `menu`, `pick` (the map list), `style[:tab]` (the map style window, tab 0-3: ground, sky, horizon, time), `mapstyle:ground,NAME[,sky,NAME,horizon,NAME|none,palette,NAME,hour,H]` (applies that style at once: the map is rebuilt, which restarts the script's frames, so a mapstyle that already holds does nothing; names without an extension get the field's; without `palette` the palette follows the ground, sky and horizon as in the window), `newmap:name,Listed name,tiles,metres` (makes that flat map in `addon\` and opens it), `saveas:name,Listed name`, `exit`, `saveform` and `newform` (open the Save as and New flat map forms). Each stroke prints its frame times (`editor stroke: ...`). |
| `I76_EDITOR_SCRIPT` object commands | capture | `buildtool:select` or `buildtool:CATEGORY`, `palette:CATEGORY[,search]` (Buildings, Structures, Roadside, Props, Vegetation, Fences, Markers, Scripted), `arm:name[,team]`, `marker:Spawn|Regen|Flag|Checkpoint[,team]`, `turn:degrees`, `place[:keep]` (what a click on the ghost does), `snap:roads|grid|off`, `flatten:0|1`, `cut:0|1`, `team:N`, `finish:0|1`, `search:text`; `put:name,x,z,yawDegrees[,roads]` places an item there at once (snapped to roads when asked), `spawn:x,z[,team[,yawDegrees]]`, `regen:x,z[,yaw]`, `flag:team,x,z[,yaw]`, `check:x,z[,99]`; selecting and editing: `pickobj:x,z` (the object nearest that ground point), `addobj:x,z`, `box:x0,z0,x1,z1`, `moveobj:dx,dz[,copy]`, `rotobj:degrees`, `del`, `copy`, `paste` (then `cursor:x,z` and `place`); a scripted mouse for the drag paths: `mdown:x,z[,shift][,alt]`, `mmove:x,z`, `mup:x,z` (the ground point's place on the screen). Each placement, move and bulldoze prints `editor objects: ...`. |
| `I76_EDITOR_SCRIPT` road commands | capture | `frame:road:CMD,args`: `mode,straight\|curve\|freeform\|select\|upgrade\|bulldoze` (`curve` twice: cubic), `click,x,z[,1]` (a click at that ground point through the snaps; 1 is Shift), `finish`, `build,ground|bridge|tunnel`, `piece,NAME|auto` (the model a bridge or tunnel stretch uses), `stretch` (log the stretch being planned), `cursor,x,z` (or `off`: the preview follows it), `select,x,z`, `drag,x,z,toX,toZ[,single]` (moves the nearest point of the road there), `insert,x,z`, `delete`, `split,x,z`, `bulldoze,x,z`, `upgrade,x,z,kind`, `kind,n`, `grade,percent`, `shoulder,m`, `conform,0\|1`, `snap,angle\|grid\|roads\|structures,0\|1`, `log[,all]`, `structures` (the ends found), `pieces` (the intersection pieces). A finished road, a drag and the like push undo steps and repaint as the mouse would. |
| `I76_EDITOR_SCRIPT` path commands | capture | `frame:path:CMD,args` (the path tool, docs/map-editor.md "Paths"): `mode,edit\|new\|insert\|delete`, `off`, `layer,0\|1` (SHOW PATHS), `cursor,x,z` (or `off`: the draft's last leg and the markers follow it), `click,x,z[,double]` (a click at that ground point in the mode: in NEW PATH it adds a point), `finish` (as Enter), `select,x,z`, `drag,x,z,toX,toZ` (the point nearest x, z moved there, one undo step), `insert,x,z` (a point on the line nearest x, z), `delete,x,z` (the point there), `deletepath,x,z` (asks, as DELETE does; the dialog is the editor's), `label,NAME` (the selected path's), `log` (every path's first entry, label, copies and points, and the table's size and override: `editor paths: ...`). |
| `I76_SWITCH_TIMING=1` | debug | Stamps each stage of a switch between the map editor and its playtest (`switch: STAGE @MS ms`, wall-clock milliseconds since the key; the build's stages are `I76_BUILD_TRACE`'s, and the editor's first frame and when its ground is drawn) and, at the first interactive frames, the memory: the resident set (split into anonymous, file and shared pages, and the C allocator's used, free and mapped bytes), the engine's, the renderer's textures and buffers, node, orphan, resource and object counts, the managed heap and what the session store holds (`Editor/SwitchTimer.cs`). `I76_SWITCH_MEM=1` collects the managed heap before each memory line, to tell what the collector can free from what is held. |
| `I76_PLAYTEST_RETURN=N` | capture | F5 is pressed N frames after a playtest opened, through the window's input path: the playtest goes back to the editor (with an editor script that starts the playtest, say `3:playtest;130:playtest;...`, the two switch over and over; a reload-mode run (`I76_EDITOR_STASH=0`) restarts the script at each return instead, `3:playtest` alone loops). |
| `I76_EDITOR_STASH=0` | tuning | The map editor's playtest reloads the scene both ways instead of keeping the editor's scene aside (the earlier behaviour, 2-3 s each way; for timing before and after). `I76_WARM_AGAIN=1` rehearses the effects behind a playtest's loading card every time, not just the first of a look in a run. |
| `I76_PLAYTEST_DECK` | capture | `1`: an editor playtest (F5) drops the car on the highest drive-on surface at the camera's pivot (a bridge's deck) instead of the bare ground. |
| `I76_EDITOR_PICK=NAME` | capture | The editor's map list picks that map by itself on its third frame (the shell's Map Editor entry, then this: the whole path from the main menu, with `I76_SHELL_INPUT=move:60,448@10;click:60,448@14`). |
| `I76_GEO_VIEW`, `I76_GEO_BOX`, `I76_GEO_SEARCH`, `I76_GEO_BUILD` | capture | The editor's New map from real world dialog (open it with the editor script `geoform`): `lat,lon,zoom` for the map's view, `latA,lonA,latB,lonB` for the box (the view fits it), a place name searched on its third frame, and a map name: the dialog fills it in and presses BUILD on its 400th frame (it downloads and writes into the install's addon folder, so point `I76_INSTALL` at a writable copy). |
| `I76_PLAYTEST_LOG` | debug | Any value: every 20 frames the player's car position and speed (`playtest car: frame N at x, z speed v`), in a playtest or a drive: to see whether a placed object stops the car. |
| `I76_EDITOR_DEBUG` | debug | `1`: log the terrain redraw (chunks stale, jobs, versions) and each surface refresh. |

## Tests and scripted runs

| Variable | Kind | What it does |
|---|---|---|
| `--skip-cutscenes`, `--mute` | test | Command-line switches (after `--`) for unattended runs: `--skip-cutscenes` skips a single-player mission's skippable scripted camera scenes as Esc would, when they start (a network game still follows its host); `--mute` mutes the master audio bus for the run, leaving the saved audio levels alone. |
| `I76_END_MISSION` | test | `win@frame` or `fail@frame`: end the mission as the script would (on a network co-op listen host, the server's mission: every player gets the end). |
| `I76_SCRIPT_MOVIE` | test | `name.smk@frame`: ask for that movie as a script's playMovie does (T05's `Ang05f01.smk`, T06's `ang06f01.smk`), the game paused under it; needs `I76_SHOT_MOVIES=1`. |
| `I76_CHECKPOINTS` | test | `1`: Checkpoints on for the run, whatever the setting (docs/savestate.md). |
| `I76_CHECKPOINT_AT` | capture | A checkpoint at that tick as well (once; not in a retry). |
| `I76_CHECKPOINT_SHOT` | capture | Save the frame a checkpoint was kept in (with `I76_CHECKPOINT_AT`, that one) to this path, cars drawn at the tick, before the notice. |
| `I76_REPLAY_AT=N` | test | Chooses Replay Mission from the menu N frames into a mission, in every load: a run of restarts, with the memory line (`replay: ...`, as `I76_SWITCH_MEM`'s) before each, to see whether a restart leaves anything behind. |
| `I76_MALLOC_DEFAULT=1` | tuning | Leaves the C allocator's limits alone (Linux). Without it, blocks of 1 MB and more go straight to the system's mappings and return to it when freed, so a freed scene does not leave holes in the heap that grow the process with every playtest or restart. |
| `I76_GC_STRESS` | test | `1`: a thread collects the managed heap's young generation and runs the finalisers over and over (the whole heap every hundredth time); `N` > 1 waits N ms between. For finding an engine wrapper (a collection, a resource) that is freed while the engine still reads it. Such faults show only where the engine's C# API is a release build, as in an export: the editor binary's own GodotSharp (`GodotSharp/Api/Debug`) keeps every argument alive to the end of a call, so for a capture copy the editor binary next to a `GodotSharp` folder whose `Api/Debug` holds the `Api/Release` assemblies and point `GODOT` at it. |
| `I76_CHECKPOINT_RETRY` | test | Choose "Retry from Checkpoint" at that frame, if one is kept (not again in the retry). |
| `I76_CHECKPOINT_HOLD` | capture | `1`: a retry's restored scene holds still (no ticks, Modern Physics' body held), to be captured against the checkpoint's frame. |
| `I76_NET_TEST` | test | `players[,seconds]`: run a network game headless-style for a test, then quit. |
| `I76_LOBBY_SHOT` | capture | `PLAYERS[,FRAMES]`: a game in a co-op lobby (`--host-listen --coop M --coop-lobby`, or joining one) saves the screenshot, FRAMES (20) frames after the lobby holds that many players, and quits; a network game does not count frames towards `--quit-after` while it loads. |
| `I76_NET_TEST_SECONDS` | test | Length of the lossy-network bot test in `I76.Net.Tests` (default a minute). |
| `I76_NET_LAG` | test | `ms[,loss%[,jitter]]`: join through a local lag and loss proxy (jitter in ms, a tenth of the delay by default). |
| `I76_NET_PREDICT` | test | `0`: no prediction of the own car in a network game (drawn 100 ms behind with the others), for A/B and debugging. |
| `I76_NET_CARS` | test | `1`: a log line a frame for each remote car in a network game: where it is drawn, its speed, its send interval in ticks, the extra delay of its clock, the correction being drawn away and its extrapolated frames (`CarTrack`), for smoothness checks. |
| `I76_NET_PREDICT_FIRE` | test | `0`: the own car predicted but not its shots: its flashes, sounds, rounds and flames drawn as the server sends them, 100 ms behind (docs/multiplayer.md, "Own shots at once"). |
| `I76_NET_NEAR` | test | `0`: the other cars near the own predicted car are drawn on their own clocks (100 ms and a round trip behind) instead of at the predicted present (docs/multiplayer.md, "The cars near the own car"). |
| `I76_NET_DEBUG` | test | `1`: the prediction's last error, correction and offset under the ping, each correction logged, and every 30 s a summary with the own shots (drawn at once, taken back, the server's drawn, handed over); `2`: also a line every input tick. |
| `I76_NET_LOAD` | test | `LoadTests`' player counts (`8,16,32`); with `_LINKS`, `_SECONDS`, `_AI`, `_ARENA`, `_RATE`, `_PREDICTED` (all by default), `_UPLOAD` (the host's upload limit, Mbit/s), `_DELTA=0`, `_INTEREST=0` and `_OUT` (docs/multiplayer.md, "Bandwidth"). |
| `I76_NET_BENCH` | test | `DeltaTests.SnapshotWritingCost`: the server's snapshot writing and simulation per tick; `I76_NET_BENCH_SEATS` players (32), `I76_NET_BENCH_BUDGET` bytes a snapshot (none). |
| `I76_ANET_SERVER` | test | The anet game server binary built from the LGPL source (docs/multiplayer.md "Testing against the real server"): `I76.Net.Tests`' anet tests run against it on 127.0.0.1 as well as the in-process fake, and `tools/anet-test.sh` needs it. Never a community server. |
| `I76_TEST_BOOST` | test | `v`: the car's horizontal velocity set to v m/s along its heading every tick once it moves (with `I76_TEST_ROUTE` or `I76_TEST_HEADING`): a car faster than any in the game, to cross a 51 km map (nearly73km: 240 gets a rebase every 10 s). |
| `I76_TERRAIN_AUDIT` | debug | `1`: every frame, whether each chunk that should stand within 350 m of the camera does (made, in the tree, visible, with a mesh); an `audit:` line when it does not and when it is whole again. It does not see a frame whose ground is all there but shown in the wrong place (the rebase's, fixed: Main.Rebase, SimDriver.PlaceCars); for that pipe the frames (`I76_SHOT_PIPE`) and look for sky where the ground should be. |
| `I76_TEST_CRUISE`, `I76_TEST_STEER`, `I76_TEST_HEADING`, `I76_TEST_BRAKE`, `I76_TEST_ROUTE`, `I76_TELEMETRY` | test | A scripted drive (`game/Scripts/Sim/DriveTest.cs`) and its per-tick telemetry line; `I76_TELEMETRY=phys` also logs Modern Physics' heading and steer at every 120 Hz step (`ptel`). `I76_TEST_ROUTE="SPEED;x,z x,z ..."` drives round a route by the rule of `i76 airtime` (with `--melee --mission M --drive` and `I76_SPAWN_AT`, the car alone). |
| `I76_RUN_TRACE`, `I76_RUN_EVERY` | test | `i76 run`: trace one object by name, every N frames. |
| `I76_RUN_ACTORS` | debug | `i76 run`: list the mission script's actor labels and the objects they name, and each thread's start and arguments (`-2` the last, as argLocal reaches them). `--player-at X,Z,YAW@S;X,Z,YAW@S` moves the player's car at those seconds. |
| `I76_WAKE_ALL` | debug | `i76 wakecheck [SECONDS] [MISSION...]` (sleeping cars against their scripts, issue #43: driving before the script wakes them, firing asleep, a radar contact while hidden; every scripted mission by default): also list the cars without findings, with when each woke and first fired. |
| `I76_RUN_DRIVE=AHEAD[,SIDE]` | debug | `i76 run` (T03): after the intro, steer the player's car at a point AHEAD units along Taurus's heading (negative: behind) and SIDE to his right. |
| `I76_RUN_WAYPOINTS=NAME` | debug | `i76 run`: print each new waypoint list of the cars whose name contains NAME (`b` marks a bridge point). The trace line shows the waypoint's bridge flag and the leg/end/arrived flags too. |
| `I76_RUN_POKE=I=V@S[;...]` | debug | `i76 run`: set the mission script's data word I to V at mission second S (T10: `14=1@36` stands for luring the cops away). |
| `I76_RUN_STRUCT=NAME[,NAME]` | debug | `i76 run`: each change to the root or node HP of the structures whose name contains a NAME, with the impacts that tick; their health on the periodic lines. `--destroy NAME@S[;...]` destroys the first structure of a name at mission second S, as the script's `destroy` does. |
| `I76_RUN_HEAL=NAME[,NAME]` | debug | `i76 run`: refill the armour and chassis of the cars whose name contains a NAME every tick. `--hurt` picks the first live car of a name. |
| `I76_RUN_GLUE=RIGHT,AHEAD` | debug | `i76 run` (T03): keep the player's car pinned at that offset in Taurus's frame, moving as he does. |
| `I76_MOTION`, `I76_INPUT`, `I76_MOTION_CSV` | test | Formats of motion traces and input spans, shared with the maintainers' original-game tooling. |
| (`i76 airtime`, `i76 ground`) | test | `i76 airtime MISSION "x,z x,z ..." [--speed S] [--rate HZ] [--seconds N]`: the player's car alone driven round a route, a row a tick with its airborne flag; `i76 ground MISSION FILE.csv` adds the ground under each row's x,y,z. |
| `I76_NATIVE` | debug | `1`: `i76 bg` reads a Nitro database by its own record numbers. |

## Rendering and performance (A/B switches, none set in play)

| Variable | Kind | What it does |
|---|---|---|
| `I76_FRAME_PROFILE` (`_EVERY`, `_SKIP`) | debug | `1`: print where a frame's time goes (GPU passes, CPU steps); print interval (60) and frames skipped (30). |
| `I76_DEV_SIGNALS` | capture | `1`: the Developer panel's Signals shown over the game from the mission's start (its "show over the game"), with the panel on for the run (`--dev`). |
| `I76_LOG`, `--log` | debug | `1` (or `--log` after the `--`): write `user://logs/godot.log` for this run though Graphic Detail's Log switch is off (the default; `Boot/LogFile.cs`, `Boot/StartupSettings.cs`). The setting itself is `shell.cfg` `[port] log_file`, written for the next start into `user://log_settings.cfg`, which `project.godot`'s `application/config/project_settings_override` names (Godot reads file logging only at start); `max_log_files` is 3. Forced on, the game's own logger writes the same file (no engine banner). stdout is unaffected. |
| `I76_VRAM=SECONDS` | debug | A `vram:` line every SECONDS: texture, buffer and video memory, object, resource and node counts, and once which VRAM formats the GPU takes (s3tc, bptc, etc2, astc). To see what grows. |
| `I76_SOFT_COMPRESS` | debug | `1`: the road layers by our BC3 encoder (`Imaging/Dxt5Encoder.cs`) as an export does, `0`: left RGBA8, in an editor build (otherwise Godot's compressor). |
| `I76_PREBAKED=1`, `I76_PREBAKED_FILE=PATH` | debug | The terrain material pack from `res://ground/pack.bin` (made by `--bake-ground-pack PATH`, which `tools/release.sh` runs) as an export reads it, in an editor build; `I76_PREBAKED_FILE` reads another file. |
| `I76_HITCH_MS` | debug | Frame time in ms above which a frame prints a `hitch:` line (default 200; below that the line also gives the terrain update time, GC counts and how many render pipelines the frame compiled (by kind: canvas, mesh, surface = made with the instance, draw = in the draw call, specialization = in the background), the time the effect views took (a phase of its own), and a terrain jump logs `terrain sync build`; a car made logs a `spawn` line with the ms of each step). |
| `I76_RUMBLE_LOG` | debug | `1`: a `rumble:` line for every level or stop the game sends to the pads (after the rate limit and Rumble Strength; with the Developer panel on and no pad, what a pad would get), to check the force feedback without feeling a pad. |
| `I76_NO_WARMUP` | debug | `1`: leave out the effects rehearsal behind the loading card (Main.WarmEffects: an explosion, a death with its fire and smoke, made on stand-ins far below the world), and the 3D garage's warm-up on the main menu (GarageWarmup: the melee form's car on an unseen stage), to measure the first-use hitches they remove. |
| `I76_GPU_TIME` | debug | `1`: measure the main viewport's GPU render time. |
| `I76_SSAO`, `I76_SHADOWS` | tuning | `0` turns the effect off. |
| `I76_SHADOW_DIST`, `I76_SHADOW_SPLITS`, `I76_SHADOW_ATLAS`, `I76_MSAA` | tuning | Shadow distance (m), cascades (1, 2 or 4), atlas size, MSAA level. |
| `I76_MODERN_MOON` | tuning | `0`: the modern look's night sky shows the mission's moon sprite instead of the drawn moon. |
| `I76_CAM_AT_LIGHT` | capture | `1`: the free camera (a mission without `--drive`) starts looking at the mission's sun or moon. |
| `I76_DROP_SHADOWS` | tuning | `0`: cars and scenery cast into the sun's shadow map again. |
| `I76_DROP_REACH` | tuning | `M`: the modern look's drop shadows reach M metres (Shadow Detail Low 250, High 500 by default; 130 was the old reach). |
| `I76_SKY_REALTIME` | tuning | `1`: redraw the sky radiance every frame. |
| `I76_SOFTWARE`, `I76_PALETTE_GRADE` | capture | `1`/`0`: Graphic Detail's 1997 Renderer: Software and Palette Grade on or off for the run, whatever the settings say. |
| `I76_SW_LINES` | tuning | The Software renderer's grid height (480); `0`: the full resolution. |
| `I76_SW_GRADE` | debug | `0`: the Software renderer without its final palette pass (the shading and assets alone). |
| `I76_PALETTE_GRADE_STRENGTH`, `I76_PALETTE_GRADE_DITHER` | tuning | The modern Palette Grade: how far toward the palette colour (1) and the ordered dither's amplitude (0.08; 0 none). |
| `I76_HORIZON_PANELS` | tuning | `1`: the sky's horizon panels instead of 3D distant terrain. |
| `I76_HORIZON_TINT` | debug | `1`: the 3D distant terrain drawn flat magenta, to see where it shows (it must never show over the map's own ground). |
| `I76_HORIZON_MASK` | debug | `0`: the distant terrain drawn over the map's tiles as it was before the floor grid (HorizonTerrain.FloorGrid), for before captures. |
| `I76_OUTSIDE_CHUNKS` | debug | `0`: no chunks of the ground past the map near the camera (OutsideTerrain): the modern look's ring alone draws it, the 1997 look nothing (the simulation still drives on it), for A/B frame times. |
| `I76_OUTSIDE_DEBUG` | debug | `1`: OutsideTerrain prints a line every 30 frames: the look, the eye, the chunks wanted and drawn, the jobs running and the levels. |
| `I76_BAKE_CACHE` | tuning | `0`: no bake cache: road layers, ground materials and terrain surfaces are made afresh and not kept (`user://cache/terrain/`, keyed by their inputs, 1 GB at most). |
| `I76_GROUND_MATERIALS` | tuning | `0`: procedural ground only (and a painted ground without the asphalt scan). |
| `I76_GROUND_PAINT` | tuning | `0`: a painted ground (M15's parking lot, GroundMarkings) drawn as fitted noise like a natural one. |
| `I76_MAT_COMPRESS` | tuning | `0`: uncompressed material arrays (editor builds only). |
| `I76_PROP_MAPS` | tuning | `0`: no material maps on scenery. |
| `I76_VEG` | tuning | `0`/`off`, `low`, `high`: the modern look's vegetation at that level whatever Graphic Detail says (docs/modern-vegetation.md). |
| `I76_VEG_PLANTS` | capture | `0`: the vegetation's plants and their shade not drawn, and no tumbleweeds; its sward (the grass worked into the ground) kept, so the ground looks as with vegetation on, bare (`I76_VEG=off` loses the sward too). |
| `I76_SWARD` | tuning | `0`: no sward on the modern ground under the vegetation (docs/modern-vegetation.md, The sward). |
| `I76_TERRAIN_CASTERS`, `I76_TERRAIN_LOD` | tuning | Terrain shadow caster level (`drawn`); finest terrain LOD allowed. |
| `I76_TERRAIN_DEBUG`, `I76_TERRAIN_STATS`, `I76_HORIZON_DEBUG` | debug | Terrain shader debug view (`1` where the ground materials are, `2` a grey ground: the light alone), terrain statistics, horizon timing output. |
| `I76_VEG_DEBUG` | debug | The plants' shader debug view: `1` grey plants, the light alone (with `I76_TERRAIN_DEBUG=2` plants and ground are lit side by side on one albedo), `2` the baked ambient occlusion (docs/modern-vegetation.md). |
| `I76_MOVIE_DEBAND`, `I76_MOVIE_DEBLOCK` | tuning | `1` turns on movie debanding; `0` turns the deblock off. |
| `I76_MODERN_ROLL`, `I76_MODERN_TUNE`, `I76_DEBUG_MODERN` | tuning | Modern Physics: start rolled over by N degrees, set tuning fields by name (`GripScale=0.7,...`), debug output. |

## Developer panel

A plain panel over the paused mission for testing and comparisons, off for players. It is on with `--dev`,
`I76_DEV=1`, or `developer=true` under `[port]` in the user's `shell.cfg` (no menu sets it; `developer_tools`, the
key's older name, is still read; true, 1, on or yes turn it on), in single player only (a network session refuses its
actions: `SessionOptions.DebugHooks`). **Ctrl+F2** or **Ctrl+`** in a mission (the backquote for a Mac keyboard, whose
F-keys need fn, and for the Godot editor, which takes Ctrl+F2 for its own menu bar), or the escape menu's **Developer**
row (the bottom of the Options Menu's list, the free line under its last entry, shown only when it is on), opens it;
Ctrl+F2, Ctrl+` or Esc closes it. The game stays paused while it is open.

| Row | What it does |
|---|---|
| status | tick, rate, the next tick's frame delta and its vehicle substeps, sim time, fps, the car's place, heading, speed and health |
| Scene | a mission the install offers (the F9 list's: the mission folder's files, so the Nitro Pack's in that game) and a car (this car's file, the mission's own, or any .vcf the install and the addon folder have); Start leaves this mission and starts that one as `--mission X --drive car` would, through the escape menu's Replay path (the F9 list's way: no shell launch, the mission's script; a Nitro melee file runs as an auto melee). The car holds for that mission's replays until a shell launch or the F9 list. The panel stays on |
| Teleport | the mission's own places (each script path's first point, heading along its first leg; each named car the script knows, 20 m behind it), or x, z, heading in game units and degrees from +Z toward +X; Here fills in the car's own. The car lands on the bare terrain there, upright and at rest (`UprightReset`'s placement, as the script's teleport) |
| Teleport | the places of the mission, in this order: the user's kept ones ("kept: ..."); the generated landmarks (below); the script's own (each path's first point, heading along its first leg; each named car the script knows, 20 m behind it). Or x, z, heading in game units and degrees from +Z toward +X; Here fills in the car's own. The car lands on the bare terrain there, upright and at rest (`UprightReset`'s placement, as the script's teleport). A name and **Keep** keep the car's place for this mission in `user://dev-places.cfg` (section the mission, `places` a list of `name\|x\|z\|heading`), listed first from then on, the newest at the top; **Forget** drops the kept place selected |
| Car | Repair & refill: armour, chassis, every part and what its damage lowered, the tyres, every weapon (handguns too) and its ammunition, the specials' charges, with no rand() drawn. God mode: Play Options' Unlim. Ammo, Armor and Chas. Rein. together, kept over the options. Bring nearest car: the nearest other living car 30 m ahead, facing the player, its AI and orders kept |
| Loadout | each of the car's weapon mounts (hardpoint, its facing or roof / dropper, from the body's file) with the weapon on it, and a choice of every weapon the garage lets onto that mount (`ChassisRules.WeaponSlot` / `WeaponsFor`: a dropper only on a dropper mount, a turret only on a roof mount, the shell's banned weapons left out). Apply swaps the changed ones live (`DevTools.SwapWeapon`, between ticks): the new weapon whole and loaded on the same mount, in the same WEPN and damage slot, the car's mass changed by the weights' difference. An empty mount stays empty (that would change the car's make-up). The car is still drawn with its old guns; the weapons panel shows the new ones. A snapshot names each weapon, so one taken after a swap restores only into that loadout: a checkpoint's retry after a swap rebuilds the car from its file, is refused and starts the mission over. Refill ammo & specials: every weapon's ammunition and the specials' charges, nothing repaired |
| State | Store: the whole session's snapshot (docs/savestate.md), kept in memory for the run (a Replay of the same mission can still restore it), with the car's weapons. Restore: the stored weapons back on their mounts first, then written back into the running session; the views re-sync as a checkpoint's retry does (car nodes made or dropped, every car's damage look, the trip's salvage). A structure destroyed after the store stays drawn destroyed although the sim's is whole again |
| Time | Freeze (no ticks; the cameras still move, Ctrl+F5 flies), Step 1 / Step 10 ticks, and a speed x0.1 to x4 (the ticks are the same at any speed; only their pace against the clock changes) |
| Signals | (the second column, with Settings) what the game sends to the player's devices over the last 3 seconds before the panel opened, a value and a plot each: the pad's two motors (low, the strong one, and high), the telemetry feed (packets a second, the last packet's pitch and roll, the car's surge, sway and heave in m/s², with its targets; "no feed" without one). While the panel is on for the run the force-feedback effects run without a pad, sending nowhere, so the traces show what a device would get. "Show over the game" keeps the Signals in a frame at the window's top right while the panel is closed (`I76_DEV_SIGNALS=1` from the start) |
| Settings | the `shell.cfg` [port] keys no pad sets (`game/Scripts/Ui/DevSettings.cs`: `net_names`, `telemetry_format`, `dev_fps`, `developer`), two to a line, a button each that steps the value as the file writes it (a right click steps back; `*` the port's): saved to `shell.cfg` at once; the key's colour says when it shows, green at once (`dev_fps`, the frame rate over the game), amber the next mission (`net_names`, `telemetry_format`), grey the next start (`developer`). A change runs the escape menu's Done path (the drawing's and the play options'). The same section is on the main menu's panel. A change is not a cheat |

**On the shell's main menu** the same keys (shown only when it is on) open a Developer panel of its own
(`game/Scripts/Shell/ShellDevPanel.cs`) with **Saves** and, in a second column, **Settings** (the in-mission panel's). Saves holds the tools that stood on Load Bookmark before. Import
Saves lists the original game's bookmarks in the install (docs/playing.md, "Bringing in saves"); Import makes an I26
bookmark of each and goes on to Load Bookmark. All Scenes, after asking, makes a bookmark per mission of the trip in that
mission's own stock car (docs/deviations.md, "Scene bookmarks") and opens Load Bookmark. Both are the base game's only
(the Nitro Pack has no trip). Neither marks anything a cheater's: they only write bookmarks, as Save Bookmark does.

The generated landmarks (`src/I76.Sim/Session/DevLandmarks.cs`, read from the mission's terrain and structures when the
panel first opens; nothing of a map is written down): **start** (where the player's car was placed), **highest ground**
facing downhill, **lowest ground** facing up the slope, **map centre** (the reached sample nearest the middle of the map's
tiles), and each structure the mission places (ODEF types 0xb to 0xd: ramps, bridges, water towers, by the placement's
name) seen from reached ground just outside it at about its own height. Reached ground is the heightfield flooded from the
start over samples without the impassable flag (0x1000, which the AI's routes keep off) and no step between neighbours
steeper than 4.2 units in 5. Highest and lowest are taken from its open part: at least 100 units from an impassable
sample or the map's edge, and no steeper than 15 degrees. The canyon walls carry the flag along their crests, so their
flanks, higher than anything inside (T05's walls reach 195, its hill 124), drop out; if what is left within half a tile
of the map's edge is still higher than the inside, that outer ring is dropped too. T05's highest ground is the top of the
lone hill east of the river, and a car put there stands on it (`DevLandmarksTests`).

The sim's side is `src/I76.Sim/Session/DevTools.cs` (tests: `DevToolsTests`, the in-place restore replaying the stored
run tick for tick). Any action but Store marks the attempt a cheater's, as the Play Options cheats do; the end menu's
Turn off Cheater Options clears it. Not there yet: freezing the AI and spawning new cars (a melee's Bot Cars and
`I76_MELEE_OPPONENTS` make cars; `I76_SPAWN_AI_AT` places them for captures).

## Debug output

| Variable | Kind | What it does |
|---|---|---|
| `I76_BUILD_TRACE` | debug | `1`: stamp each mission build stage on stdout (and the first frames drawn after it; under a briefing, Load During Briefing, the main thread's slices over 10 ms, `slice: N ms after STAGE`, with the GC's pauses in them); `hash`: also hash what the bakes made (road layers, ground materials, distant terrain), to compare two builds or a cached bake with a fresh one bit for bit. |
| `I76_STREAM_DEBUG` | capture | `1`: a map that fills the grid streams its terrain chunks (TerrainNode, docs/deviations.md): prints each look at the camera (chunks held, jobs in flight) and the mean cost of a chunk's parts every 100 made. |
| `I76_VEG_STATS`, `I76_VEG_PROBE` | debug | `1`: the vegetation's placement cell counts after the first chunks, and once a second the plants attached, nodes and plants per detail tier; the tumbleweeds' count, bursts, catches on shrubs, restarts, the CPU time a frame and the nearest one; with `I76_VEG_PROBE=x0,z0,x1,z1`, the plants of each layer in that game rectangle. |
| `I76_STATE_HASH` | debug | `N`: log the simulation's state hash every 100 ticks up to tick N (`state hash: tick T HASH`), to compare two starts of a mission with the same `--seed` (Load During Briefing on and off start the same game). |
| `I76_BUILD_PACE` | debug | `0`: a mission built under its briefing (Load During Briefing) keeps the player's frame cap and VSync instead of up to 240 frames a second under Mailbox VSync; the build then waits a drawn frame for each call to the renderer that returns a value (two to three times as long). |
| `I76_DEBUG_CAM`, `I76_DEBUG_PLAYER` | debug | Scripted camera and player debug lines (`SimDriver`), with each change of the player's tyre sounds. |
| `I76_MUSIC_PROBE` | debug | `1`: log every music player four times a second. |
| `I76_MUSIC_SEQUENCE` | setting | `retail`: the 1997 game's mission music (N..14, then 2..14, each restart on a 60 s poll); `gog`: the 1998 GOG build's (the mission's track, then track 2 on a 60 s poll). Default: on to the next track at once, wrapping to track 2 after the last. |
| `I76_SHADER_SMOKE` | debug | `1`: draw every shader of `game/Shaders` once and quit; the renderer's log shows any it refuses (with `--rendering-method gl_compatibility`, docs/compatibility-renderer.md). |
| `I76_EXIT_WATCHDOG` | debug | `0`: leave out the watchdog that ends a run when the engine hangs in its own exit. |

## Telemetry out

| Variable | Kind | What it does |
|---|---|---|
| `I76_TELEMETRY_UDP` | setup | `[FORMAT@]HOST:PORT[,...]`: once a drawn frame, a UDP datagram to each target about the player's car (single player and network games alike). HOST is a numeric IPv4 address or `localhost` (no name lookup). Off unless set; shell.cfg `[port] telemetry` takes the same text when the variable is absent. A packet that cannot go at once is dropped; nothing waits. Not `I76_TELEMETRY`, which is DriveTest's log line. |
| `I76_TELEMETRY_FORMAT` | setup | The format of the targets without one: `i26` (default), `outsim` or `outgauge`; shell.cfg `[port] telemetry_format`. |

For example `I76_TELEMETRY_UDP=outsim@127.0.0.1:4123,outgauge@127.0.0.1:4124` feeds motion and dashboard software that reads
Live for Speed (FlyPT Mover, SimHub, SimTools: set it to the LFS source on those ports). The pose is the one drawn: between
the last two ticks at the frame's alpha (Modern Physics' own body when it runs). Velocity is the sim's, interpolated;
acceleration is its change over the last tick, without gravity; the body rates come from the last tick's turn of the car.
Units are the game's (1 unit = 1 m), seconds, radians. All packets are little-endian with no padding.

**`i26`, version 1:** a 176-byte frame, then `event count` events of 24 bytes (at most 32: those of the ticks since the last
packet; more are dropped).

| Offset | Type | Field |
|---|---|---|
| 0 | char[4] | `I26T` |
| 4 | u16 | version (1) |
| 6 | u16 | frame size (176): events start there |
| 8 | u32 | packet number |
| 12 | u32 | sim ticks since the mission started |
| 16 | f32 | alpha: how far the frame is between the last two ticks |
| 20 | f32 | sim seconds of the pose drawn |
| 24 | u32 | status bits: 0 a player car, 1 engine on, 2 airborne, 3 sliding, 4 dead or disabled, 5 cut-scene, 6 paused, 7 network client, 8 lights, 9 handbrake |
| 28 | u8 | gear: 0 reverse, 1 to 4 forward |
| 29 | u8 | wheels laying skid marks, bit per wheel: 0 front right, 1 front left, 2 rear right, 3 rear left |
| 30 | u8 | blown tyres, same bits |
| 31 | u8 | event count |
| 32 | f64 x3 | position x, y, z (world: y up; heading 0 faces +z) |
| 56 | f32 x9 | attitude rows: right, up, forward (world vectors) |
| 92 | f32 x3 | heading (from +z toward +x: positive turning right), pitch (nose up), roll (right side down) |
| 104 | f32 x3 | velocity, world |
| 116 | f32 x3 | acceleration, world |
| 128 | f32 x3 | acceleration in the car's frame: right, up, forward |
| 140 | f32 x3 | body rates: pitch (nose up), yaw (turning right), roll (right side down), rad/s |
| 152 | f32 | speed |
| 156 | f32 | engine rpm |
| 160 | f32 | throttle 0..1 |
| 164 | f32 | brake 0..1 |
| 168 | f32 | steer −1..1, +1 full left |
| 172 | f32 | health 0..1 (the damage gauge) |

An event: u8 kind, u8 a, u16 b, u32 tick, f32 v0..v3.

| Kind | a | b | v0..v3 |
|---|---|---|---|
| 1 shot by the player | the weapon's class | the weapon's place in the load order | the muzzle's direction (world), 0 |
| 2 hit on the player | damage channels (bit 0..3) | 0 | the damage, 0, 0, 0 |
| 3 collision of the player's car | 0 ground or a body, 1 a car, 2 scenery | 0 | relative speed, the contact normal (world) |
| 4 explosion anywhere | 0 | 0 | distance from the player, the offset from the player (world) |

**`outsim`:** Live for Speed's classic OutSim, 64 bytes (68 with an id): u32 time ms; f32 angular velocity x, y, z; f32
heading (anticlockwise from above), pitch, roll; f32 acceleration x, y, z; f32 velocity x, y, z; i32 position x, y, z in
1/65536 m (wrapping past 32 km). Its axes are x and y on the ground (x the game's x, y its z) and z up.

**`outgauge`:** Live for Speed's OutGauge, 92 bytes: u32 time ms; char[4] car (the .vcf's first four letters); u16 flags 0;
u8 gear (0 reverse, 1 neutral (engine off), 2 first...); u8 player id 0; f32 speed, rpm, turbo 0, engine 90 C, fuel 1,
oil pressure 0, oil 90 C; u32 dash lights available (full beam, handbrake, oil warning) and lit (the lights, the
handbrake, health under a quarter); f32 throttle, brake, clutch 0; char[16] display 1 (the selected weapon), display 2
empty.
