# Changelog

## Unreleased

### New
- Upscaled textures for the world, the cars and the cockpit: a texture pack placed in the game's user folder (`tex-world-4x`, `tex-world-8x` and the like, or an `i76-hd-textures*.zip`) is drawn on the scenery, the cars and the 3D cockpit in the modern look, in place of the original textures it has. None comes with the game; the 1997 look and a game without a pack are unchanged. `hd_textures=false` under `[port]` in shell.cfg turns it off.

## 0.12.1 (10 November 2026)

Network games use protocol 24: 0.12.1 does not play with 0.12.0 or earlier, so everyone in a game needs 0.12.1.

### Fixed
- Driving off the edge of a map works as in the original again where it matters: off a high edge the ground falls away to the plain below, as the original's does, so a car driven off a plateau drops (and a fall of more than 35 m wrecks it) and custom maps built around their sheer edges play as they were made. A low edge still runs on smoothly into the land beyond, and the distant hills and mountains now start well away from the map.
- A mission's briefing plays to its end unless you skip it: the mouse wheel or a resting finger on a touchpad no longer counts as a skip (which made the mission start the moment it had loaded), and a key or button already held when the briefing starts only skips it once released and pressed again.
- A mission's first CB line no longer plays during its briefing (Taurus in T02 could be heard halfway through it); it plays when the mission starts.
- Quitting in the first seconds of the main menu no longer crashes the game.
- The modern look's smoke can no longer make the graphics driver fail on cards that refuse the way it reads the scene's depth with anti-aliasing on (this may be behind some of the crashes reported with Direct3D 12); it falls back to the previous way instead.
- Render Scale no longer asks the graphics card for more memory than it has on a large desktop: the 3D view is drawn at no more than 3840x2160.
- A race bot that is killed comes back at the start and drives the course to the checkpoint it still needs, instead of heading straight across the arena for it and falling off on the way.

## 0.12.0 (10 November 2026)

Network games use protocol 23: 0.12.0 does not play with 0.11.0 or earlier, so everyone in a game needs 0.12.0.

A big thank-you to @therealjkvalentine, whose fork brought a large share of this release, and to @lcosbr; each line below credits their work.

### New
- You can drive off the edge of a map: the land carries on from the map's edge, smoothly and with no drop, into the hills and mountains on the horizon, and you can drive on them. In the 1997 look it shows within the original's 600 m view, with the horizon and sky beyond as before; the map editor shows all of it in both looks, out to the distant mountains, with nothing cut off at any Draw Distance (a playtest looks as a mission does). Missions that fail you for leaving an area still do, and computer cars stay on the map as before.
- A mission's briefing now plays while the mission loads, so a briefing watched to its end goes straight into the mission (about 8 seconds sooner on T01 and T07). If you skip the briefing before the mission is ready, a small "PLEASE STAND BY . . ." shows in the movie's corner and the mission starts by itself the moment it is ready; a briefing that ends first waits on its last frame. Play Options' new Load During Briefing row turns it off (the original's order: load, then briefing). Online co-op, a checkpoint's retry and the map editor's playtest keep the original's order (with @therealjkvalentine).
- Mouse Options: a new menu screen, opened from the Options menu, Control Configuration or the in-mission Escape menu, gathers the mouse settings in one place: Mouse Camera, a finer Mouse Sensitivity (10% to 200% in 10% steps), Invert Y Axis, Camera Return, View Zoom Reset, Mouse In Window, direct bindings for the left, right, middle and two side mouse buttons, and what the mouse wheel does: zoom the view, or cycle weapons in both directions (wheel up goes back, wheel down goes forward). These settings have moved out of Graphic Detail. Its Cockpit Mouse Look row (off by default) lets the mouse turn the driver's head in the 3D cockpit, so you can look out of the side windows and over your shoulder (#145, thanks @lcosbr).
- Chase Crosshair (Play Options, off by default): a small crosshair in the F2 chase view shows where a shot from your selected machine gun, cannon or straight-flying rocket would land, and turns red when it would hit a car. In a network game it shows only if the host turns on the new Chase Crosshair game option (#145, with @lcosbr).
- Graphic Detail has a Resolution row: Desktop (the borderless fullscreen, as before) or a centred window of a common size, applied at once and kept for the next start (#81, thanks @therealjkvalentine).
- Graphic Detail has a Render Scale row (100, 150 or 200 %) that draws the 3D view larger and scales it down for a sharper picture, and Anti-Aliasing gains an 8x step (thanks @therealjkvalentine).
- Graphic Detail has a Field of View row (Original, 80, 100 or 110 degrees) for the chase, outside and cockpit views, applied at once; the 2D dash's gunsight follows it so it stays on the gun line. It changes only what you see, not how the game plays (thanks @therealjkvalentine).
- Online co-op has three new host options, all off by default: Co-op Damage scales the damage the players' cars take (x0.5, x0.75 or x1.5) for an easier or harder game; Co-op Scaling gives each enemy car a copy for every player past the first, up to a cap of +1, +2 or +3, so a group meets more opposition (the mission's objectives still follow the original enemies); Rolling Start brings a respawning or joining player in at the speed of the teammate they appear beside when that teammate is driving along. They are on the host form's co-op rows and in the Nitro Pack form's co-op game options, and i26-server takes --coop-damage, --coop-scale and --coop-rolling-start (thanks @therealjkvalentine).
- Ignition Lock: a network game's host can stop players switching their engine off, so nobody can hide from turrets that way. It is a row on the host form, IGNITION STAYS ON in the Nitro Pack form's game options, and --no-ignition-off for i26-server; the game list says "Ignition stays on" (#108, thanks @therealjkvalentine).
- A network game's host can ban a player for the rest of the game: type /ban NAME in the chat line to boot them and refuse their rejoins, /unban NAME to lift it, and /ban alone to list the bans; a dedicated server's console has ban, unban and bans (#117, thanks @therealjkvalentine).
- Play Options has a Turrets row: 1998 (the default, as in GOG's game and the Nitro Pack) or 1997 CD, where turrets turn at the CD release's rate, slowing with damage until a wrecked turret stands still. In a network game it is the host's choice: a Turrets row on the host form, TURRETS in the Nitro Pack form's game options, and --turrets-1997 for i26-server (with @therealjkvalentine).
- Simulation Rate offers 24 Hz, the rate the original actually ran at under its 20 fps cap on Windows NT and later. It is in Play Options, the host forms and i26-server's --rate; the default stays 20 (thanks @therealjkvalentine).
- The network host form (MELEE, MULTI MELEE, HOST) is drawn larger, as a ruled sheet in both looks, with a labelled line for every setting (Unlimited Turrets, Chase Crosshair, Share Maps, Hidden Weapons & Cars, Sim Rate, Car Reset, CB Radio, the password, the bots, the co-op options and the rest each have their own) beside the area list and your car's weapons, parts and picture. The Nitro Pack's form is as before. The game list's popup now shows every one of a game's host rules, paging through them every 3 seconds when they do not fit on its line.
- JOIN > INTERNET goes straight to the game list and searches every server in your address book at once, listing the games under a heading for each server with its state (how many games, connecting, not answering). Its SERVERS button opens the address book, which now has a DELETE button.
- Play Options and Graphic Detail scroll as one list, the original's rows and I26's together, with the mouse wheel, the arrow keys, Page Up/Down or the pad, and a bar that shows there is more, instead of a second page behind a hard-to-see "More".
- Play Options has a Rumble row for a game pad (Original, the default; Enhanced, which adds short pulses for nitrous, the handbrake, gear changes, the starter, each shot and the engine; or Off) and a Rumble Strength row (25 to 150 %) (thanks @therealjkvalentine).
- I26 tells you when a newer release is out: at start-up it quietly checks GitHub and shows "version X available" under the version in the corner of the game chooser and the main menu (click it to open the release page). Play Options has a Check for Updates row to turn it off (#138).
- Load Bookmark has a Delete button that removes the picked bookmark after asking (thanks @therealjkvalentine).
- At night the modern look lights the faces the original draws self-lit: the Paradise gas station in T06 is lit and open, its canopy, pumps and forecourt bright as in its cut-scene, and street lamps, lit windows and neon glow in every night mission. Road signs are retroreflective, so in your headlamps' beam they read bright from a few hundred metres.
- Map editor: switching between the editor and its playtest is much faster. Coming back to the editor is instant (it was a 2 to 3 second reload) and going into the playtest takes about a second instead of 2 to 3 (and more on a slow machine). An edit you make always shows in the playtest.
- Map editor: a playtest (F5, the PLAYTEST button) drops your car under the mouse cursor, on the ground, a road or a bridge's deck, facing the way the camera looks; with the cursor on the sky, off the map, on water, on a cliff or in a building, or off the 3D view, it goes where it used to, at the camera's pivot, and says why.
- Map editor: a path tool (the PATHS group, keys - and =), like the path layer of Activision's Arsenal mission builder: the AI paths a mission's script sends cars along, teleports them to and tests them against show over the ground in both looks, each with its label and an arrow for its direction, and you can draw new paths, select one, drag, insert and delete its points, rename it, and delete a path you made (a path the mission's script came with stays, as the script finds it by its place). Every edit can be undone, and the paths are saved with the map.
- Map editor: F12 switches between the modern and the 1997 look, as in a mission (F2 still works too), and the game's soundtrack plays while you edit, a random track after another at the Music level of Audio Control; a MUSIC button in the top bar turns it off and on, and a playtest hands over to the mission's own music.
- A Developer panel for testing and exploring, off unless you turn it on (developer=true under [port] in shell.cfg, or --dev; Ctrl+F2 or Ctrl+` opens it): teleport to generated landmarks or your own kept places, repair, God mode, bring the nearest car, swap the car's weapons live and refill them, start any mission in any car, store and restore the moment, freeze and step the game, show the frame rate, and, on the main menu, import the original game's bookmarks (car and parts included) as I26 bookmarks (thanks @therealjkvalentine).
- Telemetry for motion rigs and dashboards: a telemetry line under [port] in shell.cfg sends your car's motion over UDP once a frame, in I26's own format or Live for Speed's OutSim / OutGauge (thanks @therealjkvalentine).
- When an optional sound pack is installed, Audio Control (in the menus and over a mission) has a HiFi Audio row to switch it on and off.

### Fixed
- Difficulty Level works as in the original: it was saved but never applied, so every mission played Champion. At Wimp (the default) your own shots and flame do double damage, so enemies die twice as fast as in earlier I26 releases; at Badass 0.75x; network games are always 1x. Helicopters are a little easier to catch with splash damage, as in the original.
- Black parts of cars are black again: the school bus's black stripes and lettering no longer come out orange, and the limo's black paint is no longer haloed grey and blue. Black paint, trim, grilles and tyres may also no longer look corrupt on some graphics cards (the reported black car textures in 0.11.0): every car is now clear-coated paint all over, with only the windows kept as glass (#154).
- The macOS build starts again: on macOS 26 it was killed as it opened ("The application cannot be opened", "Launchd job spawn failed") (#144, thanks @therealjkvalentine).
- Computer drivers no longer shoot through buildings: as in the original, their line of sight is blocked by structures, scenery, gates and standing signs, generously, and they no longer hold fire behind a low crest that the original's line of sight clears (thanks @therealjkvalentine).
- Computer cars ordered to attack a building aim at the building, not beside it: the paved lot round a building no longer counts as part of it, so the Leadsled gang now takes down T02's school instead of putting their rounds into its car park (#154).
- Turrets lead their target as the original's do, and a turret on a moving car now aims at where its target will be and hits it, where before it shot behind a car driving alongside. Damaged turrets keep their full turning speed in network games, and a turret climbs to its elevation limit for a target above it instead of standing still (thanks @therealjkvalentine).
- Computer cars fire the EZK mortar, which they never did; computer mortars lead a target as in the original, and computer droppers no longer lay oil or mines under a car chasing them straight on at more than about 100 km/h (thanks @therealjkvalentine).
- Computer drivers behave more like the original's in many smaller ways: how they predict a turning or reversing car, when they hold fire for a teammate (or a neutral car) in the line, how they steer round obstacles and back away, how often they skid-turn, and how custom scripts' attacker limits are read. In T02's opening the car behind Taurus no longer reverses and makes a three-point turn, so the camera is let go when the original lets it go (thanks @therealjkvalentine).
- With Unlim. Ammo on, a computer car that runs out of ammunition keeps firing, as in the original (thanks @therealjkvalentine).
- Taurus's car corners on the original's load-based grip, so he keeps the original's pace in the T02 race (thanks @therealjkvalentine).
- Rounds stop on road signs and posts, and an explosive hit knocks a sign down; a car meets a sign at each post instead of on one sphere round the whole sign, and the crashed plane in the Nitro Pack's P18 is solid (thanks @therealjkvalentine).
- Fences and tyre walls stand on the line between their posts, each piece centred on its stretch as in the original, instead of half a metre to one side with the gaps all at one end (#137, thanks @therealjkvalentine).
- Gun emplacements stand on the ground as in the original (some of T11's guns were sunk into it or hung above it), and in the modern look buildings and other objects stand on the drawn ground wherever they are, instead of floating or sinking on ridges and mesas no car reaches (thanks @therealjkvalentine for the gun emplacements).
- The side rails of ramps such as T06's are solid only: a car bounces off them instead of being lifted up onto them (thanks @therealjkvalentine).
- A car shoved into a wall by another car now meets the wall, as in the original, instead of being driven on into it (thanks @therealjkvalentine).
- Scenery explosions take the original's sizes: where two kinds of building share an explosion (the oil pump and the gas station), the one placed first in a mission decides how big both blow, as in the original (thanks @therealjkvalentine).
- Bullet, cannon and mortar rounds start at the muzzle, except on the occasional shot that takes the original's spread (thanks @therealjkvalentine).
- A car killed by a hard landing stays dead, as in the original (thanks @therealjkvalentine).
- A Nitro Pack mission started from the mission list without choosing a car gives you the Piranha, as in the original, instead of the mission's own car for the player; online co-op still offers the mission's own car (thanks @therealjkvalentine).
- Curb Feelers, Mud Flaps and Structo Bumper absorb damage exactly as in the original (Curb Feelers and Mud Flaps let through one point less on some hits). Heated Seats give every weapon 10% more ammunition; in the Nitro Pack, Dashboard Mary adds 10% damage to every shot and Tire Covers double the tyres' strength; Auto Melee opponents no longer get a blower boost.
- Field salvage follows the original: a dead car's destroyed specials can be salvaged, and the T16 tanks' wrecks give no later weapon and no special (thanks @therealjkvalentine for the T16 tanks' wrecks).
- A cheat turned on in Play Options before a trip mission now marks the run as cheated, as in the original, until Turn off Cheater Options: such a win no longer gives salvage or moves the trip on (thanks @therealjkvalentine).
- Mission scripts run closer to the original: a scripted wait no longer ends up to a second early, a hit or ram is seen by the script for two frames instead of one, a teleport puts the car on the ground under the path's first point, and a car the script is driving through a cut-scene is no longer pulled off its line by a flat front tyre (thanks @therealjkvalentine).
- A cut-scene's cut between two shots of the same kind is a cut, no longer a quick pan across (T01 and T04) (#140, thanks @therealjkvalentine).
- Police sirens wail as in the original: once a mission turns a police car's siren on, it keeps sounding until the mission turns it off or the car is wrecked, instead of playing once, and the trip's and the scenarios' own police cars sound the siren rather than a short car horn, as on the 1997 CD. A Sheriff that honks at a car in its way turns its siren on too.
- The cockpit radar works as the original's: the sweep shows all 30 of its frames, a car pings on every pass, the light and growl answer any car in range whatever the range setting shows, and it beeps steadily while you hold a target (#154, with @therealjkvalentine).
- The engine's pitch follows the original's: idle no longer sounds about 7 semitones low and the top end too high. The Moth trucks', buses' and tractor's engine plays at the original's 60% volume (thanks @therealjkvalentine).
- The Effects and Voice levels in Audio Control follow the original's volume curve (4 dB per halving) instead of 2 dB a step, and the line spoken when the map opens follows the Effects level, not Voice (thanks @therealjkvalentine).
- Pressing the trigger again before a weapon is ready to fire plays the dry click, as in the original (thanks @therealjkvalentine).
- Mission music goes back to track 2 after track 14, as the original does, instead of playing the disc's last tracks first (#128, thanks @therealjkvalentine).
- Your car no longer hops at the start of a mission: it is already at rest on its suspension when the loading screen lifts (#141, with @therealjkvalentine).
- The menu music no longer cuts out for a moment after the loading screen: it plays on through the last part of the loading, and the mission's own track starts without a hitch (#142).
- Rumble no longer switches itself off when a mission starts: a mission's loading, a pause, a movie or another window in front never counts as the pad blocking the game, and the brief buzz during loading is gone.
- The modern look's smoke no longer shows through a hill, a car or a turret in front of it, nor fringes its outline, which was worst with Anti-Aliasing on Apple machines (#95, thanks @therealjkvalentine).
- The 3D garage opens in about a quarter of a second the first time instead of over a second, without holding up the main menu (#143, thanks @therealjkvalentine).
- The first building explosion of a mission no longer stutters (#116, thanks @therealjkvalentine).
- 1997 look: an effect's sprite stands on the ground the right way up, as the original's (the WP mortar's burst stood upside down and half underground) (#105, thanks @therealjkvalentine).
- 1997 look: roads are lit as in the original, no longer up to a quarter darker near the camera, and keep their dashes and edges into the distance instead of fading into a smooth grey band (thanks @therealjkvalentine).
- 1997 look: a crashed helicopter disappears, as in the original, instead of lying there as a yellow hull (#154).
- 1997 look: brake lamps follow the brakes tick by tick, as the original's do (thanks @therealjkvalentine).
- The 3D cockpit's dials keep their lettering sharp when the dash is small on screen, and under the 1997 Software renderer the gauge needles are the original's dark red instead of pink (thanks @therealjkvalentine).
- The hood view (F6) no longer draws your own car round the camera, as the original does not (thanks @therealjkvalentine).
- The death and end-of-match orbit turns at the original's speed instead of about a tenth of it (thanks @therealjkvalentine).
- Once a network game is won, every car brakes with the wheel straight until the end, as in the original, instead of driving on (thanks @therealjkvalentine).
- A network melee's helicopter lifts off from its spawn point at once, as in the original, instead of waiting for throttle (thanks @therealjkvalentine).
- The network ping no longer jumps up under packet loss: it shows the lowest of the last ten round trips (thanks @therealjkvalentine).
- Online games list: a game's cars in its popup showed a code and a car file name such as "NF8023A9BVCGCOUR.VCF" for bots and players with their own variants; they show the car's make and model, in both games and in co-op, and so does the STANDINGS sheet after a network game, which left those cars blank. A game listed without a name, or with a map's file name, shows its arena's name (e.g. "Mojave Sprint").
- Co-op: a gun emplacement that has been shot down keeps its burnt shape as a solid obstacle for the other players' cars too, and a gate the mission slides open or shut moves for them as well, so a client no longer collides with an intact gun or an invisible gate and gets snapped back.
- A host no longer fails during a melee when a bot's start and goal fall in the same spot (thanks @therealjkvalentine).
- Race bots drive into each checkpoint before heading for the next, so they no longer circle between checkpoints set close together; the map editor warns when a race's checkpoints overlap so much that a car can count laps standing still.
- The CB microphone is off until you choose one (CB Microphone in Audio Control now starts on None), so the game no longer asks for microphone access when it starts; hearing other players' CB voice works as before, and the talk key tells you once to choose a microphone.
- Held driving and firing keys no longer act only on the press after the system swallowed a Shift, Control or Alt release (seen on a Mac) (thanks @therealjkvalentine).
- A right click in Control Configuration during a network game waits for a key again instead of capturing the pointer (#106, thanks @therealjkvalentine).
- Mouse In Window keeps the pointer in the window in the menus too, not only while you drive, so it no longer wanders onto a second monitor (#147, thanks @therealjkvalentine).
- Save Bookmark opened from the garage, field salvage or a mission's end screen goes back to that screen on Cancel, not to the Options Menu (thanks @therealjkvalentine).
- Memory use no longer climbs with every mission you start in one session (some 400 MB each in a big map), or each time you restart a mission or go between the map editor and its playtest.
- Less processor work each frame for the drop shadows and the modern look's smoke (thanks @therealjkvalentine).
- The modern look's distant mountains no longer vanish behind a map on high ground: each map's ring of mountains is placed once when it loads (a custom map's from its lowest ground), so they stand over the horizon wherever you drive and never shift as you move. The 1997 look's horizon mountains stand as in the original, at every draw distance and in the map editor, sinking with the camera's height.
- Custom maps: a ramp or bridge whose model the map also uses as an intersection piece is drawn again (it was invisible but solid until wrecked).
- Map editor: no more crashes after a while of big terrain strokes on a large map, and big maps build much faster (road painting about five times quicker).
- Map editor: the tool column on the left scrolls when the window is too short for it, so every tool can be reached at any window size, and a tool chosen by its key is scrolled into view; the panels on the right and the keys overlay (F1) scroll and wrap the same way (#146).
- Map editor: the 2 m wood ramp is placed the right way round on a road, climbing along the road instead of lying across it.
- The 3D garage's ARMOR page looks like the original's CONFIGURE CHASSIS form: its DEFENSE block, with a line for each of FRONT, RIGHT, LEFT and REAR with armor and chassis side by side, the [-] and [+] boxes, LBS. AVAILABLE and BALANCE, instead of eight separate lines. In the 1997 look it is the original form's own picture.
- The F10 overview's Page Up/Down zoom follows the Simulation Rate, as the original's followed its frame rate (thanks @therealjkvalentine).
- A car braking to a halt keeps its nose down for the stop and then lifts it, as in the original, instead of popping up on the stop itself (thanks @therealjkvalentine).

## 0.11.0 (31 October 2026)

Network games use protocol 21: 0.11.0 does not play with 0.10.0 or earlier, so everyone in a game needs 0.11.0.

The source code is now public on GitHub.

### New
- Graphic Detail has a Renderer row (Auto, Vulkan, D3D12 on Windows, OpenGL), applied at the next start, so a machine whose graphics driver misbehaves can switch to another without the command line.
- The modern look's night sky has a real moon: a sharp, detailed full moon (from NASA's public-domain map of its surface) with a soft cold glow, the clouds drifting in front of it, in place of the old blurry sprite (Two Days Before and the other night missions). The night sky round it is the original's deep navy with blue-violet clouds again, where the modern look had turned it a flat grey.
- Graphic Detail has a View Zoom Reset row. On (as before), the outside views' mouse-wheel zoom goes back to the normal framing when you switch views, after a cut-scene and on a retry; off, each view (the F2 chase view included) keeps the zoom you set until you start another mission. End still resets it by hand.
- The in-game Audio Control in a network game has a CB Microphone row, the same choice as the front end's: switch the input device while you play, even mid-talk, without leaving the game (#135).

### Fixed
- Online co-op: a gate a mission shuts behind you (two of Interstate '76's missions and the Nitro Pack's training course have one) no longer shuts on your teammates when the first player gets through: it stays open while anyone is still on the other side, and after 30 seconds the ones left behind are brought through beside a teammate before it shuts.
- The map editor's New map from real world downloads roads again when the main road server is overloaded: it now also asks that server's own machines and the French road server, which answer when the main address turns requests away.
- A controller's View and Menu buttons, when bound to a command in Control Configuration, now do only that command instead of also opening the standings / the menu.
- The 1997 look's ground may no longer draw almost all black, lit only along the triangle edges and striped on the cliffs, on AMD graphics under Vulkan on Windows (#133). The light table the terrain and roads read is now passed to the shaders as plain values, which that driver should handle; we could not try it on that hardware, so tell us if it still happens (the new Renderer row in Graphic Detail can switch to D3D12 or OpenGL meanwhile).
- Unpacking a GOG installer now keeps the game's own executable (i76.exe or nitro.exe), which I26 reads the computer drivers' tactics from. A game folder unpacked by an earlier version lacks it: I26 now restores it from the GOG installer if that sits next to I26 (or in its user folder), otherwise missions say what to do (put the installer next to I26, or copy the executable from your GOG install).
- A resprayed car no longer keeps a ring of its old paint round the windows: the body colour, frame and trim the windows are drawn with now take the new colours too.
- A car no longer starts a mission or a melee puffing grey smoke as if damaged, that vanished a moment later, most visible in the cut-scenes and chase views where you first see your car (#134): the smoke of the burning-wreck rehearsal behind the loading card is cleared away with it.
- The Jefferson Sovereign's left front door window and the small window before it are glass in the modern look again, where they were drawn as body with a stray triangle and a bare pillar.

## 0.10.0 (29 October 2026)

Network games use protocol 20: 0.10.0 does not play with 0.9.0 or earlier, so everyone in a game needs 0.10.0.

### New
- In the Nitro Pack, Groove (your driver on custom maps and missions the Nitro Pack does not know) has no Poem key lines of his own, so with Interstate '76 installed too the key plays one of the one-liners he says when he kills an enemy there.
- The map editor has a Surface tool: paint the ground's surface type (dirt, sand, asphalt, dirt road, planks, gravel, parking lot, dirt junction) with a round brush to change how cars grip and sound on it and which plants grow, with a coloured map of the types over the ground while you paint (the SURFACE MAP button hides it), Alt+click or I picking the type under the cursor, and undo and redo like the other tools.
- The chase camera now waits about four and a half seconds after the mouse or stick last moved before easing back behind the car, and a new Graphic Detail row, Camera Return, turns the return off so the view stays where you left it. A camera swung several turns round no longer spins back, and the stick's turning now eases back too. (#125)
- Unlim. Turrets: a new Play Options setting (on its second page, "More") that lets a car carry more than one turret, each on a roof mount, in every garage (the trip's, Instant Melee, network melee and co-op). Network hosts have the same option on their host form (UNLIM. TURRETS, also --turrets for the dedicated server), shown in the game's listing; where it is off a car with a second turret is refused when it joins and a computer driver's car keeps only its first.
- CB radio voice chat in network games (melee and co-op): hold the Poem key (C unless you rebound it in Control Configuration; a pad's Share / Mic button too) and talk, and the other players hear you over a CB radio, band-limited and squelched, with a click of static at the key, on Audio Control's Voice level. In team games only your team hears you; everywhere else everyone does. The microphone is not opened until you first press the key. The handset comes up on your screen while you talk, and a CB panel shows who is on the air. Play Options' second page has CB Voice (off neither sends nor plays) and Audio Control has CB Microphone; type /mute NAME or /unmute NAME in the chat line to silence a player for yourself, or /cb off for the game. The host can mute a player's CB for everyone with /cbmute NAME and unmute them with /cbunmute NAME (everyone is told, and the muted player's screen says so; a name can be a unique start or part of it), and can turn the CB radio off for a whole game with the CB Radio option on the host form (shown in the game's listing).
- A key to put your car back on its wheels (U by default, Reset Car Upright in Control Configuration): it drops the car onto the ground directly under it, upright, still and facing the way it was, for when a jump goes wrong. It is the old F12 trick as a proper key. In a game you host (melee and co-op, on the host form's Car Reset line or in the Nitro form's game options) you can turn it off; the game list tells joiners when it is off.
- The map editor can make a map of anywhere in the real world: MAP > New map from real world opens an OpenStreetMap you can pan and zoom (with a place search), drag a box over the area you want, choose a name, the games, how many roads and the time of day, and BUILD turns that area's real terrain and roads into a playable melee map in the addon folder and opens it, with progress and a Cancel; it needs an internet connection while building and downloads what it needs once. The same button is in the map list.
- Online co-op has a lobby: hosting a co-op game opens the Net Melee entry roster where the players gather before the mission starts. Each picks a car (the mission's own, or a car and variant of their own, even from CONFIGURE CHASSIS), marks READY, and chats; the host starts the mission (it asks first if someone is not ready), and can boot a player with Delete. Everyone then sees the briefing and the mission begins for all of them together, once every game has loaded, with the cars standing at the start. The game list shows a co-op game as "co-op lobby" until it starts; players joining a mission already running come in as before. If the host leaves the lobby it closes with a message. A dedicated server (i26-server) holds the lobby and starts the mission when everyone there is ready (it can wait for a number of players, or skip the lobby).
- Online co-op: the short camera moments that belong to one player (the jump cameras of Interstate '76's missions and the camera that follows a car driving off the edge of the map) now show on that player's screen only; everyone else keeps playing and keeps their own view and controls, instead of the whole game being held in the cut-scene. The story's cut-scenes still play for everyone.
- Online co-op: a movie in the middle of a mission (the ones in T05 and T06) now stops the game for everybody while it plays, so nobody can be hurt or timed out under it, and only the host can skip it, for everyone; another player's Esc opens their menu. A player who joins, or a new host after the old one leaves, picks the movie up where it is.
- Cars in the modern look have real windows: the glass painted on every car of both games is now glass, tinted the colour it was painted, reflecting the scene, with a soft view into the cabin behind it, the driver at the wheel turning it as the car steers (his hands where the cockpit's steering sprite puts them) and the car's seats (two rows in four-door cars, rows of benches in the bus, side benches in the limo). The rest of each car takes the finish of what it is: paint, chrome, steel, rubber, lamp lenses, canvas (the Leopard's top, the hearse's curtains) and the Bushmaster's matte. Graphic Detail's new Car Interiors row turns the cabin view off, leaving plain reflective glass.
- The hidden F4D Phantom II jet (Hidden Weapons & Cars) has a turret mount under its belly: a turret hangs upside down under the fuselage and covers everything below and around the jet (it never aims up through the hull).

### Changed
- Saving a car in the 3D garage is quicker: pressing DONE after changing a stock car opens its name with a free one ready ("Custom 1"); type over it and press Enter (or DONE) to save, or Esc to go back. A car with a new name is saved straight away, without the "Wanna save the new variant?" question. Overwriting one of your own cars still asks first.
- The map editor's New map from real world opens over the game's own trip, from Seagraves, Texas, across to Tatum, New Mexico, and down past Pecos to Fort Davis, instead of over Billings, Montana.
- The network game HUD is quieter: the ping, the lap and place counters, the score and goal, the flags in Capture the Flag, the time left, the game's listing ("Listed on ...") and the key reminders now show only while you hold the standings key (Tab, or a pad's Back), where the chat, self-destruct and CB keys are also listed. Over the game you still see the kill feed, the chat, who is on the CB and a warning when the connection is in trouble.
- The chase view (F2) no longer pulls back and widens as the car speeds up in the modern look: it stays at the distance you set.

### Fixed
- The map editor's New map from real world no longer seems to hang at "Downloading roads": a road server that takes the connection but never answers is left out after a few seconds instead of being waited on for minutes each time, a busy one is retried, and the progress line shows which server is working and for how long (a big area can take a minute or two).
- In the Nitro Pack the Poem key makes your driver say one of their lines, as the original does.
- The menu music no longer plays on through a briefing or a cut-scene, in single player and in online co-op: it stops when the movie starts, and the mission's own music begins after it.
- In the 3D garage the two helicopters (Hidden Weapons & Cars) now stand on their skids on the turntable instead of floating above it on a single wheel; their Wheels row reads None.
- Mission music goes straight on to the next track when one ends, like a CD on repeat, instead of sometimes leaving up to a minute of silence first. (#128)
- Driving on dirt, sand or gravel (and skidding or a flat tyre) no longer drowns out the engine and your guns: the tyre sounds now sit at the engine's level, as quiet against it as in the original. (#127)
- The Nitro Pack's Net Melee host form lists its areas on the form's ruled lines now: the names under the highlighted one no longer sit on top of the rules, and clicking a row picks the row you see. (#132)
- Random static or crackle during play, worst on headphones: loud moments (an explosion over the engine, several guns at once, even ordinary driving) added up past full volume and the sound clipped; the mix now goes through a limiter. (#130)
- The 3D garage shows helicopters, jets and other very big vehicles whole: they are scaled down to the size of the bay's largest car (and the Armadillo up), so the overview, the orbit and the weapon and part close-ups no longer run into the walls (#126).
- The chassis form's DONE now refuses a turret on a helicopter's side (Fwd) gun mount, as it does on every car's, so the 2D and 3D garages agree: a turret goes on a roof mount only (#131).
- The CB microphone list on Linux names each device as the system does ("Yeti Stereo Microphone Analog Stereo") instead of its internal name (alsa_input....).
- A gamepad or joystick keeps driving and firing while the chat line is open in a network game; only the keyboard goes to the chat.
- The Limo (and other long cars) no longer spin in fast donuts when driven forward after Reset Car Upright on a slope.
- A few cars no longer show a hard change of shading where the body's sections meet (behind the Leprechaun's door, along the limo's side, on the Street Van, Skeeter's Van and the bus).
- No more stalls around the first fight: the first moments of play, the first bullet to hit a car and the first car of each model to blow up are all prepared behind the loading card (the cockpit, HUD and weeds started up, every weapon's hit effects and sounds, every car's burnt look and flat tyres).
- Online co-op: a player driving off the edge of the map no longer leaves the story's camera stuck on the whole game.
- The ground, plants and buildings no longer vanish for a couple of frames every couple of kilometres on a long drive (at Draw Distance Original the screen showed only sky for a moment; at Far the world jumped), the "terrain disappears mid-game" report, in both looks.
- Community maps that fill the whole 51 km grid (nearly 73km) load in a few seconds instead of a minute or more: their roads are painted into the ground as it comes into view rather than for the whole map at the start, and the ground round the start is made while the mission loads.

## 0.9.0 (27 October 2026)

Network games use protocol 18: 0.9.0 does not play with 0.8.0 or earlier, so everyone in a game needs 0.9.0.

### New
- CONFIGURE CHASSIS is a garage: the car stands on a turntable in a concrete bay, the camera flies to the part you are choosing (a wheel, a gun mount with a ring over it, one side of the body for the armour, the engine bay), you can turn it round and zoom with the mouse or the pad's right stick, and a small notepad menu in the old form's style (its handwriting for every name and its plain face for labels, its list frames, arrows and red pencil, its PARTS CATALOG, DONE and CANCEL buttons, drawn sharp at any resolution) offers MAKE / MODEL, VARIANT, PAINT SCHEME, WHEELS, WEAPONS, SPECIALS, ARMOR, ENGINE, SUSPENSION, BRAKES and NAME. MAKE / MODEL and VARIANT are the melee form's lists: pick another car and it takes the turntable (it asks first if you changed this one; CANCEL still leaves the car you came with). F12 switches between the modern and the 1997 look in the garage, as in a mission. Whatever the cursor is on is shown on the car before you pick it, Esc steps back, and DONE asks and saves the variant exactly as the old form did. Wheels can now be any car's, front and rear apart, as a style (named by how it looks and which car wears it) and a size (13in Stock to 16in Billets) for each axle. The gun lists offer only what the old form's DONE would pass: a hardpoint that is not a roof turret mount never offers a turret. A boombox on the workbench plays the games' soundtrack at the Music level, a random track after another, as if in the room, while the menu music waits. It draws in both looks and is on by default; Graphic Detail's 3D Garage row brings back the original's form.
- RESPRAY in the garage paints a car in a colour of your own: pick the primary paint (and, on schemes that have a second colour, the secondary) from 24 classic 1970s American car colours (Hugger Orange, Grabber Blue, Plum Crazy, Sublime, Rallye Red, Forest Green and more) or make one with hue, saturation and brightness, in a gloss, metallic or matte finish, and see it on the turntable before you choose it; Esc undoes it, FACTORY gives the scheme's own paint back. The colours are saved with the variant (next to its file, which stays as the original game wrote it) and show on the car in Instant Melee, in single-player melees, as a bot's car and online, where everyone in the game sees your colours. The 1997 look shows the paint snapped to the original's palette.
- Online co-op story missions take up to 8 players instead of 4: the host form's NO. DRIVERS, the dedicated server and the command line go up to 8, and the extra cars line up behind the lead without overlapping.
- The chase view (F2) zooms with the mouse wheel, smoothly, from close behind the car to well back, and every mission and every switch into the view starts at the original's distance. With Mouse Camera on, the mouse swings the view round the car and, after about a second without moving, it eases back behind it, as in GTA V. In the modern look the camera also falls back a little and widens its view at high speed (the 1997 look keeps the original's camera). The mouse wheel zooms the other outside views too (the front and front-quarter views, the target and reverse views, the side views, the bumper view, the missile view and the top-down view), each starting at the original's framing; End puts the view on screen back. (#115)
- The hidden F4D Phantom II jet (Hidden Weapons & Cars) can be armed: the garage offers a gun mount under each wing and a belly dropper, all firing along the jet's nose.
- Modern look: plants follow how the ground drives, as the original's ground clutter does. The rough ground (the big patches of it in the Seminole missions, the deserted town and a few others) grows thickets of soapweed yucca and cholla, in groups, over sparser grass; road-surface ground (the dirt flats round T14's crossroads, plank roads, parking lots) stays bare dirt, as in the original, apart from the weeds and grasses along the road's edge and the river bottom's growth.
- Your weapon link stays on when you die and come back in a network melee, a co-op mission or Instant Melee, with the same weapon selected and linked as before when the new car has the same guns.
- Ctrl+T "show all names" starts on in network and co-op games (press it again to hide the names); your last choice is remembered between games.
- Modern look: the Goon (the army jeep) is in flat paint, as its camouflage should be, instead of a shiny clear coat.
- A one-time notice at the start when Arial or Impact is not installed, saying the menus use a stand-in font and suggesting the Microsoft core fonts.

### Fixed
- Modern look: the game no longer freezes for a moment the first time something blows up, a bridge or building is destroyed, a car is damaged or you die (falling off a cliff included): the explosion, wreck, fire and smoke effects are prepared while the mission loads, which adds a fraction of a second to it. (#116, #118)
- Modern look: the shadows of cars and buildings now reach much further, to 250 m at Shadow Detail Low and 500 m at High (they faded out by about 130 m), the far ones drawn as a single cheap layer. (#119)
- A car's soft shadow no longer has holes where a tilted or airborne car (an open pickup bed above all) is turned from the sun, and the Hacienda's pickup bed is matte black liner again, not a mirror streaked with sky (#123).
- Modern look, online games: the strip at the top of the screen (your place, the score, the time, the key hints) and the kill feed no longer slide off the right edge when the game runs on a second monitor. (#120)
- The PARTS list on the melee and scenario forms shows the car's tyres (e.g. "13in Stock") after the engine, suspension and brakes, as the original does, instead of its specials.
- Velocity (the Nitro Pack's P17): the bus and the other cars in the opening cut-scene no longer sink through the ground, also while the intro cuts between shots and in online co-op.
- Cannon rounds no longer look like machine-gun bullets: as in the original they are a fat, fiery streak (the 1997 look draws it from the cannon round texture), while machine guns fire thin tracer lines. Each machine gun's tracer has its own colour again, as in the original: white for the 30 cal, yellow for the 50 cal, red for the 7.62 (and green for the Nitro Pack's 12.5 mm), in both looks.
- 1997 look: the small bushes and tufts the original scatters over the ground round you (black tufts among the saguaros on the training mission, grey-green bushes along the road) are drawn again, in the same places and lit the same way.
- Fan-made maps that fill the whole map grid, like nearly 73km (one terrain tiled over 6400 zones, 51 km across), now load in seconds and play: the ground is made as you drive toward it, as far as the draw distance reaches (600 m at Original, 7 km at Far, fading into the sky's horizon), instead of the game running out of memory and time loading all of it, and the road that starts past the map's edge is drawn. nRoute380 (the base game's level and its Nitro Pack races) is checked to load and play as well.

## 0.8.0 (24 October 2026)

Network games use protocol 16: 0.8.0 does not play with 0.7.0 or earlier (or the 0.8.0 test builds), so everyone in a game needs 0.8.0. Saved states from 0.7.0 do not load in 0.8.0.

### New
- Online co-op story missions, from the menus or the command line: MELEE → MULTI MELEE → HOST has a co-op form (the Nitro Pack's GAME TYPE Co-op; the base game's AREA OF PLAY bar's Co-op) that lists the story missions by their titles, for one to four players, and the JOIN roster shows co-op games tagged Co-op and joins them like a melee. Each player picks a car ("Mission's own car" is the first choice and the default) and the briefing plays before you host or join. `--host-listen --coop t01` hosts a story mission (either game's, `p01` for the Nitro Pack) for up to four players and `--connect HOST:PORT` joins it (`i26-server --coop MISSION` for a dedicated server). The host's game runs the mission; everyone sees its cut-scenes, hears its radio, has its objectives on the notepad and gets its end. A destroyed player comes back beside a teammate after five seconds, and a player who drives off the map is warned and brought back; the mission fails when everyone is dead or the script says so. In a cut-scene only the host's Esc skips; the other players' Esc opens the menu. Network games use protocol 15.
- Instant Melee takes up to 31 A.I. drivers (it was seven), a 32-car game like the online one's: the drivers' list scrolls past seven rows (mouse wheel, pad, a thin bar at its edge), and cars beyond the arena's spawn points are spread out so none start on top of another. nituser.mel still holds what the original can read (eight at most). (#100)
- Online bots: the host picks each bot's car and variant, in an A.I. CARS popup on the host form (the base game's Bots line, the Nitro Pack's GAME OPTIONS) or with `i26-server --bot-car N=MODEL[:VARIANT]`; each bot seat keeps its car as bots come and go, restricted weapons are taken off, and a bot left on Random under WEAPON RESTRICTIONS prefers the host's saved variants that carry no restricted weapon.
- Modern look: the Nitro Pack's Ball O'Flame is a roiling ball of fire (hot white core, cooler orange edge) with a tail of flames, embers and a little soot, a warm flickering light that lights the ground and cars it passes, and a burst of fire where it lands. The 1997 look keeps the original's fireball card.
- Map editor: the road tool builds on the ground, a bridge or a tunnel (BUILD ON in its panel, Shift+Tab, or the BRIDGE and TUNNEL buttons under the road tools). A bridge is laid in whole deck pieces end to end on one grade, over a valley, river or road: click the start, click toward the far end, and the span snaps to the pieces that fit best (or the one model you pick), pitched so that the decks join, with a pier of ground raised under each joint, the road under it cut at its ends, and warnings when the deck is buried, the grade is held back or the bridge is off the map's axes. A tunnel is one piece, level, with the road running through it and the ground levelled under its mountain; it warns when it would stand on fill or cut deep. The preview shows the pieces over the ground; the whole thing is one undo step. Bridges and tunnels are straight (curves are laid straight); ground roads go on from their ends as from any bridge.
- Map editor: tunnels (short, medium, long) are laid over a road like a bridge, along it, the road running through the tube and left as it is, the ground levelled under the mountain; a BRIDGE / TUNNEL button under the road tools opens the structures tab with road snapping on. The base game's palette gains the Nitro Pack's bridges and ramps.
- Map editor: the object picker labels things with the Complete Asset Bible's descriptions ("Building No.100(large)", "Oil pipeline") and keeps the code in the tooltip; the search finds them by description too, and the palette credits DIVER, Skull N. Bones and Brian Dean Jennings. 51 objects no stock map places (the Industrial Park buildings, the cemetery, the cattle fences, the tunnels, shrubs, signs and more) join the palette, marked "not in any stock map".
- The simulation rate is a host game option: the multiplayer host forms set it (GAME OPTIONS' SIM RATE, or Rate on the base game's form; `--host-listen --rate HZ`), everyone in the game plays at it while connected, and it is shown in the join roster and the game's popup. It is kept apart from Play Options' rate, which your own single-player games keep. Network games always use the original's physics for everyone (GAME OPTIONS says so): Modern Physics needs the server to simulate it too. (#90)
- Control Configuration lists the multiplayer keys: Chat (online, Backspace), Self Destruct (Ctrl+Delete), Respawn (online, Space) and Show All Names (Ctrl+T), each rebindable (click a row and press a key, with Ctrl, Shift or Alt if you like), with a hint on the left saying that Backspace chats and Ctrl+Delete then Space brings a new car. The choices are kept in I26's own file and laid over gamekey.map, which is left alone. (#98, #93)
- The log file is off by default: Graphic Detail's Log row turns it on (from the next start); `--log` or `I76_LOG=1` turns it on for one run, for a bug report. When it is on, the game keeps three logs.
- The INTERNET address book lists interstate76.com's anet servers again (Glenrio and Interstate76.com) beside BZ911 and Iondriver; a book saved by an earlier version gets them once, and one you delete stays deleted.
- A Linux arm64 release (the game and the dedicated server; ASTC textures for ARM graphics), for a Raspberry Pi 5, ARM laptops and the like.

### Fixed
- A melee wave, a bot's replacement or your own respawn stalled the game for a tenth of a second or more as each car's model was assembled again (and its lamps' glow read back from the graphics card): the cars' models are built once a mission and reused, and a network game prepares the bots' and A.I. cars' behind the loading card.
- Release builds kept the modern look's road and terrain textures uncompressed (the export has no texture compressors), so they took more than twice the video memory of a development build (T01: 899 MiB against 391) and filled the logs of smaller graphics cards with texture-memory errors. The road layers are compressed when the mission loads and the terrain's are baked into the release, so a release uses what a development build does.
- Online: a game with bots (capture the flag above all) stopped for everyone: the host saves the game every second and a half for whoever takes over if it leaves, and a bot just given a new route made that save fail and the host's game with it. The save takes such a route now, a save that still fails is skipped and tried again, and a player who cannot take over a game ends it with a message instead of crashing.
- Toggle Weapon Cam on a helicopter: the turret's view lay on its side, the camera following the mount's frame, which the helicopter carries rolled; it now keeps the gun's line and the horizon level.
- Nitro Pack: choosing a tyre on the chassis form fits the other wheel of the same axle too, as the original's form does (the base game's sets the one wheel, as its own does).
- The PARTS list on the melee, scenario and Net Melee host and join forms was always empty: it now names the variant's engine, suspension and brakes (and its specials), under WEAPONS, as the original does.
- Modern look: the terrain no longer freezes the game for a quarter second or more when the camera jumps (a cut-scene or script teleport, the camera returning from a death view): the terrain levels it needs are built in the background, nearest first, a few uploaded a frame, and a chunk shows a coarser level meanwhile (#116).
- Bots and the A.I. fill in a game with weapon restrictions drive their own cars with the restricted weapons taken off (all of them under All Weapons), the same car on every machine, the host's own screen included; with All Weapons there were no bots at all, and the A.I. fill kept its guns.
- When your car is destroyed in a mission or a scenario the instruments, target brackets and CB handset disappear, as in the original, leaving the death orbit alone; in a network game they come back with the car on respawn. (#92)
- A first death in a game stalled it for a quarter of a second as the wreck was built and its fire started (a big fall on Nitro's Grape Vine, say); the wreck's model and the fire's materials are now made at the mission's start, behind the loading card, which halves the hitch. (#92)
- Modern look, cockpit view: a missile or rocket that hit your own car showed no explosion; the blast was cut away inside the cab box that hides smoke drifting through the driver. Blasts and fireballs are no longer cut (smoke still is), so a hit on your car shows through the windows and in the mirror. (#94)
- Sound: a sound plays in as many voices at once as its file says, as in the original (the file's header gives a priority and a count, one for a plain .wav): past that a new one stops a playing voice of lower priority or is dropped. Four linked cluster bombs' fifty bomblet blasts no longer stack into one deafening roar. (#110)
- Map editor: a copy of a story mission's map saved under a new name (the Crater, A Lot of Asphalt and the other base-game melee maps are m01 to m15 `.msn`) was written as `.msn`, which neither game's melee list reads, so it never showed; it is saved as `.lvl` (`.cbt` in a Nitro Pack install) now. A copy made before keeps its `.msn`: rename it to `.lvl` (or `.cbt`). (#96)
- Network HUD: long chat lines and other lines no longer run off the screen. Both looks wrap them (a word wider than the window is broken, the Say line scrolls to its end), the modern look's feed rows, prompts, standings columns and end banner keep inside the screen at any size or aspect, and chat stays up for 30 s with a history to read in both looks (the 1997 look had none). The modern look's strip and the 1997 look's top right remind new players of the chat key and Self Destruct. (#93)
- Online (#109): after a death the view you had (the in-car view, an external view) comes back with your new car, instead of staying on the chase camera.
- Online (#109): the cars of a network game are as tough as the original's. They had the single-player player's armour (x2, parts x4) and kill roll (0.1 where a network game's is 2.5), so bots, and everyone else, took far too many shots; a healthy car now takes about 68 .45 shots, not 150.
- Online (#109): with the Esc menu up in a network game the arrow keys no longer turn the driver's head (or steer the chase camera): the game goes on under the menu, and its keys are held back while it is open.
- Online (#97): a helicopter, balloon, UFO or Phantom no longer crashes the moment it lifts off a spawn point on a slope. It was set down exactly on the ground, and the flight model (the original's) crashes it as soon as the ground ahead is above it; it starts half a metre up now.
- Online (#109): F11 (the projectile camera) works in a network game. A client draws its own rockets and missiles from its prediction, which the camera never looked at, so it found nothing to follow.
- Online (#109): a destroyed car no longer fires. A trigger held through a death kept the wreck's guns working and it could kill from the grave; the wreck's weapons stay silent now, whatever the keys say.
- Modern look: the drop shadows of buildings and bridges no longer vanish when the camera turns: a caster was kept only while its centre lay ahead of the camera within 130 m, so a long bridge or a building beside you lost its shadow once its centre fell behind the view. The nearest point of its bounds now counts, from any direction. The 1997 look keeps the original's view-depth rule (#91).
- Nitro Pack caltrops, 1997 look: a dropped patch spreads out, as the original's does (its texture is eight frames, MCALT101..108, played once at six a second like the oil slick, the jacks flying out from a clump to the full 4 m square); it stayed on the first frame. Modern look: the jacks are bigger (0.4 m), brighter and more of them, so they read on dirt (#104).
- 1997 look, Software renderer, Nitro Pack: the maps keep the horizon panels (the sunset glow and the far hills) as the GOG build draws them; the CD build's plain sky band, which that build never drew on a Nitro map, stays for the base game (#103).
- Toggle Weapon Cam (W, or F in the default keys): the radar's picture had a cream box round the dial in the dash. The weapon camera's mask paints the corners outside the radar box cream, where the original's picture leaves them clear (the dash shows through); they are clear now.
- Interstate '76 co-op host form: the Melee / Co-op switch is a button in the AREA OF PLAY bar (RENAME's look) instead of handwriting over the header, and the Bots / Co-op missions line no longer sits on the options box's rule. The trip's missions in the co-op list carry their names from the game ("1. Meet Taurus" to "17. The Truth Is Out There") instead of "Mission 1" to "Mission 17", which did not read as the trip.
- Interstate '76, T01: the attackers shoot the Wagon Wheel diner when Taurus gets there, as in the original. They aimed their cars at the corner the building is placed from, 53 m from the diner itself, so their guns pointed past it and they hardly hit it; they now drive, turn and judge range by the building's middle, as the original's cars do with any building they are sent at.
- Interstate '76, T06 (Get Gas!): the Creepers sent at the Paradise gas station keep shooting it, as in the original, and burn it down in about 40 s if you leave them be; the T01 fix above was the cause here too, their guns pointed past it. They turn on you only when you shoot them, and go back to the station a minute later: the mission's own script does that in the original.

## 0.7.0 (19 October 2026)

Network games use protocol 14: 0.7.0 does not play with 0.6.x or 0.5.0, so everyone in a game needs 0.7.0 (a host leaving hands the game over to a player as a saved state, and the two versions save it differently).

### New
- Play Options: the first time you change the Simulation Rate away from 20 Hz, a notice says that other rates can break the game (the AI, helicopters and physics were made for 20 Hz); it does not come again.
- Modern look: the cars' wheels are round. The original's wheel is an eight-sided slab with a round tyre painted on its face, so its flat edges cut across the tyre; the modern look now draws a smooth disc and tread fitted to the tyre the texture shows (a blown tyre keeps its flattened shape), in the same size and place. The tyres are matte rubber with a tread pattern (grooves and blocks that roll with the wheel) and the hubcaps keep a satin shine, no longer a clear-coated gloss all over. The 1997 look keeps the original's wheels.
- Online games can have bots: players the server drives, for when there are too few people. The host form's new Bots line (a count up to the free seats, and Easy, Normal or Hard; Nitro's form has them in GAME OPTIONS; `--bots-ai N` and `--bot-skill` on the command line and on `i26-server`) fills the game with drivers from the game's own list of names, marked "(bot)" on the scoreboard, who play the game you chose: in a fight they go for the nearest enemy and in team play never shoot their own side, in a race they drive the course lap after lap, and in capture the flag they raid, defend and escort. They score, die, come back after five seconds and use the regen pads like players, count toward the win, give their seats up as people join and come back as people leave, and carry on under the new host if the host quits. They are no match for a good driver, and the Nitro race arenas are hard on them (a few they cannot finish).
- Map editor: a STYLE window picks a map's ground, sky, horizon panels and time of day from everything the install's archive holds (Nitro's too), with a picture for each. An 8-bit ground or sky brings the palette it needs, so its colours come out right. Applying is one undo step, rebuilds the map in both looks with every edit kept, and Save writes the choice into the map in the original format, so the original game plays it too.
- Modern look: Seminole's pine trees are real 3D pines, grown to the size and shape of the original's flat cards (a bare trunk, loose whorls of drooping branches turning up at the tips, needles in tufts, a spire), where the cards stood. They are part of the map, so they show whatever the Vegetation setting says; they block and break as before, and a destroyed one leaves the original's stump. The 1997 look keeps the original's turning cards.
- Network games: the AI cars filling the arena go by instant melee's driver names (Torque Daisy, Sire Screech, ...) in the kill feed and over their roofs, instead of "AI" and their car file; so do the test bots of `i26-server --bots`.
- 1997 look, 1997 Renderer: Software: the sky draws as the 1997 CD's software mode drew it, with a colour gradient along the horizon in place of the mountain panels the later releases added. The band rises about twelve degrees over the horizon, brightest at its foot, in the mission palette's own sky blues.
- 1997 look, 1997 Renderer: Software: the radar shows every car in green, as the 1997 CD's software mode did: bright as the sweep passes or it is picked as the target, then darker green. The later releases drew the radar's palette entries, which come out in other colours.
- 1997 look: drop shadows are see-through, darkening the ground under them the way the original software renderer did, so the road and the ground show through them. The original's 3D-card renderers drew them solid black.

### Fixed
- Modern look: a thin dark seam ran round the new round wheels where the hub picture met the tread. The tyre's face is now solid to its edge, and the tread takes the tyre's own colour.
- Creeper Canyon (Interstate '76's arena and Nitro's N58 copy) no longer whites out the cockpit's HUD and leaves Escape dead: the mission names a palette file the game never had, so the dashboard had no colours and the options menu could not open; it now takes the palette its own light and shade files belong to.
- The game chooser works with a gamepad: A starts the highlighted game and B or Start quits, as in the rest of the menus.
- In the cockpit (and the binoculars and gun camera) the engine, horn, wind and tyre sounds of your own car are centred and at full volume, as in the original, instead of coming mostly from the right speaker. Other views and other cars' sounds are unchanged (the engine in outside views now fades with distance the original's way).
- Modern look, Draw Distance Original: Nitro arenas whose map names no horizon panels (Evening Stroll, Killing Field, Evening Jog and Lot of Asphalt) showed a white band where the mountains should be. They have no mountain line in the original either (the 1997 look shows plain sky); the modern look now draws the plain sky and haze there, and the mountains of Far are unchanged.
- Modern look: a dark translucent wedge hung in the air across the canyon under Seminole's truss bridge and swept across the view as you moved. It was the bridge's drop shadow, whose corners either side of the canyon's rim were joined by one sheet; the shadow is now cut small and the pieces that do not follow the ground are left out. The same went for other bridges and ramps over a drop.
- The police cars' light bar (and the Sheriff's bar on every car that has one) floated a few centimetres above the cambered roof, visibly at its ends from the side; it is now seated on the roof.
- Cluster bombs (and other mortars) on a forward side hardpoint on the left of the car pointed down and blew up on the car instead of arcing up. The original turns the muzzle round when it mounts a weapon on the left side; the port now does too.
- The Nitro Pack's Ball O'Flame was drawn as a plain bullet streak. It is now the original's fireball: a flame card that always faces the camera and plays its six-frame fire animation, in both looks.
- Damage smoke follows the car's health bar, as the original's does: a car burnt red by flame (which hurts the engine, suspension and brakes, not the armour) now smokes black, so you know it can be finished with the .45. In the modern look the smoke from your own engine no longer fogs the whole windscreen from the cockpit.
- A car that died with its guns firing kept firing for good, until its ammo ran out, even after the fire key was let go. Its weapons now stop when the key is released, as in the original.
- Nitro Pack host form: GAME OPTIONS is a popup of its own in the shell's look (boxes, - and + buttons, DONE and CANCEL) instead of the VARIANT list whose DONE did nothing and which closed on every click; each option changes where it stands, DONE keeps them and CANCEL or Esc puts them back.
- Dark seams ran between the panels of the cars: down the front fender to the wheel arch, behind the door and under the side stripe, black in the 1997 look (the Piranha shows it best). The panels meet exactly in the game's data and the original draws them that way; the port lifted some panels a centimetre off their neighbours, a fix meant for lamps and windows set into a wall that also caught panels merely lying in the same plane. Now only faces that really lie over another are lifted, and the cars close up again in both looks. Some buildings, tunnels and bridges built of panels in one plane lose the same small steps.
- Nitro Pack caltrops looked like oil slicks: the drop wore the oil's black texture instead of its own (MCALT101, white spikes on clear). The 1997 look now draws that texture, as the original does; the modern look draws real 3D caltrops, small four-spiked metal jacks (one spike always up) scattered over the patch on the drawn ground, casting shadows. Where they blow tyres is unchanged.
- 1997 look: Seminole's pine trees were flat cards that showed edge-on as thin slivers from some sides. The original turns them about their trunks to face the camera (any node flagged so in its model), and so does the port now, lit as the original lights them; so do the burning wrecks' flame and smoke planes and the fires of a wrecked oil pump or gas station (in the modern look too, where those still show).
- The police tank's cannon is a turret again, as on the 1997 CD: it turns on its own to follow the radar's target, for the player and the AI tanks alike. The 1998 build (GOG, Nitro) renumbered the turret guns and missed the two tank cannons, which have stood fixed since, the whole tank turning to aim.
- Modern look: the damage smoke covers what is behind it. Headlamp and tail lamp glares (drawn with no depth test) came out over a plume however far behind it they were. The smoke is now drawn first of the transparent things and the glares dim themselves where smoke lies in front of them, so a lamp behind the smoke is veiled and one in front still shows.
- Modern look: smoke can no longer go solid. The damage smoke, the burning wrecks' smoke, the rocket exhaust and the chemical cloud are drawn into a layer of their own (at half size, behind the same depth test against the scene) and laid over the scene with their total opacity capped at 65%, however many puffs overlap, so a car behind a plume stays in view. I76_SMOKE_CAP changes the cap. This needs Vulkan or D3D12; in the Compatibility renderer each puff is capped on its own instead, thin enough that a column stays under about 90%.
- Modern look: the smoke is lit by the night's lamps. Headlamps, brake lights, sirens, a wreck's fire and the flashes of a blast (the nearest 16 scene lights) light the smoke they shine through, with a forward-leaning scatter, so a beam through a plume reads as a lit volume. I76_SMOKE_LIGHT scales it.
- Modern look: smoke shows in the rear-view mirror again (the smoke layer does not run on the mirror's camera, so the mirror draws each smoke plainly, lit and thinned). In the Compatibility renderer (OpenGL) the smoke is lit by the same lamps, and the overlap of puffs is thinned by how many lie on one spot of the screen, so a column no longer goes solid.
- Modern look: the FireRite rocket's blast, and every other volumetric explosion, are drawn when game/vfx has its textures but no import cache (a copied folder): they were read as missing, and the old sprite explosion showed in their place.
- A wrong `--install` folder (one that doesn't exist or holds no game) now prints one line saying so and quits, instead of filling the log with errors every frame.
- 1997 look: a big black slab stood upright in the portal at the far end of Seminole's truss bridge as you drove onto it. It was the bridge's drop shadow, laid on one plane under the bridge's centre, which over the canyon is the canyon wall; shadows of bridges and ramps now fall on the terrain under them, vertex by vertex, as in the modern look. The same went for a car on that bridge: its shadow plane came from the canyon's terrain, not the deck, and fanned a huge black wedge up between the trusses; a car's shadow now lies on the deck it drives on (in both looks), and no shadow plane is ever steeper than 45 degrees.
- Modern look: paved roads no longer take the terrain's bump map, which made them look bumpy, most of all in headlights at night. Asphalt is now shaded by the ground's slope taken over a broad span, so the original height data's small lumps no longer show as a wavy track either; dirt roads, planks and the ground beside the road keep their relief.
- Modern look: the parking lot of M15 and Nitro's A Lot of Asphalt and Asphalt maps drew as a flat pale ground with no asphalt, paint or lanes. Their terrain shader no longer compiled since the map editor's brush cursor was added (the cursor's code was left out of the lot variant while the terrain used it); both looks now match as before.
- 1997 look at Far: the sky's horizon mountains broke into vertical blocks at different heights that jumped up and down as you drove (Seminole shows it well). Their foot followed where the ground ends one degree at a time, measured again every 60 m; it now runs smoothly along that end and moves with the eye.

## 0.6.1 (16 October 2026)

Network games still use protocol 13: 0.5.0, 0.6.0 and 0.6.1 can play together.

### Fixed
- Modern look at the Far draw distance: the distant mountain ring drawn over everything, around and in front of the car, on some graphics drivers (NVIDIA among them; #69). Its shader wrote a depth on one path only, which leaves the depth undefined on the others; it now writes one on all of them. New in 0.6.0.

## 0.6.0 (15 October 2026)

Network games still use protocol 13: 0.5.0 and 0.6.0 can play together.

### New
- Graphic Detail scrolls. The port's own options had run out of room and some were squeezed in as small text beside other rows (Crash Blur beside Palette Grade, VSync beside Max FPS), where they were hard to find. On the main menu's sheet the original's six rows stay where they were drawn and the port's rows scroll under them, one option to a line, with Crash Blur and VSync as rows of their own: use the mouse wheel, Up and Down (Page Up and Down) or the pad's D-pad, and a thin bar shows there is more. The in-game menu's Graphic Detail scrolls the same way and now has the Crash Blur row, which takes effect at once.
- Graphic Detail: Crash Blur (On, Off; on by default). The original's software renderer ghosted the whole screen, HUD included, for about 1.4 s when the player's car took a big hit in the cockpit view (the frame blended 50/50 with itself moved up and left by 19 pixels, easing out); On brings it back: the original's exact effect in the 1997 look (either renderer), a smooth ghost in the modern look. The jolt now works without the wiggleburger cheat.
- Map editor: the ground under a road takes the road's surface, as in the stock maps (asphalt, dirt or planks within 10 m of the centre line), so the grip, bumps and tyre sounds on a road you draw are the road's. Drawing, extending, moving, retyping and bulldozing a road change it in the same undo step; the ground under intersection pieces is kept.
- Map editor: intersections. A road drawn or moved to end on another road, or across one, gets the game's own intersection piece (a T or X square to the road, a fork at 41 degrees, in asphalt or dirt) at the road's height, and the roads are cut at its arms as the stock maps' are, all in one undo step; the preview shows the piece, road ends snap to a free arm, and moving, retyping or bulldozing a road changes or removes its pieces (an X that loses a road becomes a T; the two roads of a dissolved piece join again). The ground under a piece takes the stock intersection surface. The rules place 784 of 784 stock pieces as the maps have them.
- Map editor (milestone 3: objects). Place buildings, props, plants, fences, signs, bridges and ramps, and the markers a melee, race or CTF map needs. The OBJECTS tools (`M` select, `P` build, `J` spawn, `R` regen, `Y` CTF flag, `Z` race checkpoint) open a palette with a tab for each group, a search box and a picture of every item (drawn once and cached), built from what your install's own maps place (287 items on the Nitro Pack alone; scenery that only the campaign uses is dimmed, vehicles, guns and gates that need a mission script are in their own tab). The thing follows the cursor as a ghost, red where it overlaps another object, sits on too steep ground or on a road; the wheel or `,` `.` turn it by 15 degrees (Shift: 1); near a road it snaps to the road's side facing it, set back from the edge, and a bridge or ramp goes onto the road, along it, and cuts the road at its ends as the stock maps do (the road tool's own bridge and ramp ends); `N` switches road, grid or no snapping, `L` levels the ground under it, all one undo step. Click selects, Shift adds, a drag on empty ground boxes, dragging moves, Alt+drag copies, Del bulldozes, Ctrl+C / Ctrl+V; a small inspector edits the name, type, heading and a spawn's or flag's team, and re-orders race checkpoints. Spawns are numbered, flags are in team colours (as many teams as the map has), checkpoints show their 30 m ring and the lap's direction, and a map check lists what the mode needs (a spawn for every team and a flag for each team in CTF, checkpoints 1..N without a gap in a race, never more than 64 spawns or regen pads). F5 plays the map with what you placed, with collision.
- Map editor (milestone 1: terrain). Main menu: MAP EDITOR (or F8) opens a list of your maps and every mission; Edit one, or start a New Flat Map (1-4 tiles a side, a spawn in the middle). RTS-style camera (WASD, right-drag to orbit, wheel to zoom toward the cursor, F to focus), six terrain brushes (raise, lower, smooth, flatten, level to a typed or picked height, roughen) with four falloffs, a brush cursor drawn on the ground in both looks, `[ ]` and Shift+`[ ]` for radius and strength, Ctrl+Z / Ctrl+Y undo and redo a stroke, F5 plays the map from where the camera looks (F5 or Esc comes back), F1 lists the keys. Save writes the map to `addon\\` in the original formats; a stock map is never written over, it asks for a new name. Roads and object placement come later.
- Map editor (milestone 2: roads). Keys 7 to 0 (or the ROADS palette) open the road tool: draw straight roads (Shift chains pieces), quadratic and cubic curves and freeform splines, snapped to 15 degrees, a grid, the end of an existing road (the new road extends it, or closes a loop), the ends of bridges, jumps and ramps (square and at their height) and, with a hint that the game has no junctions, the middle of a road. The preview is the real road mesh in both looks with the ground's steepness against your maximum grade in green, yellow and red; the ground under the road is cut and filled to a smoothed profile held to that grade, with a blended shoulder, and the road and ground are one undo step. Select a road to drag its points (the road bends about them), add, delete and split points, change its type or bulldoze it. Only the terrain chunks a road touches are repainted. Roads are written in the original RSEG format (10 wide, 5 apart, left edge, loops), so they load in the Nitro Pack and I26, F5 drives them, and the AI routes along them.
- Online games: hold Tab (or Back / View on a pad) for one standings view in both looks, instead of `'` and `;`, which sat in different places on different keyboards. In team play each team's totals head its players, and the view's header carries what the old mode panel showed (mode, time left, goal and leader, lap and place, ping, who hosts, CTF's flags). The modern look's always-on mode panel is gone; a one-line strip at the top (time left, your place, CTF flags) and the kill feed stay, and both now start at the top of the screen or just under the radar, weapons and damage panels instead of halfway down.
- Reverse is on X now (it was Tab, which holds the standings). Your saved controls are moved once if reverse was still on Tab (a copy is kept as input.map.pre-scoreboard); a key you chose yourself is left alone.
- Map writers (the foundation of a map editor; no menu yet): missions (.cbt .rac .cf2-4 .msn .lvl) and terrain (.ter) can be edited and saved back in the original formats, so an edited map loads in I26 and in the original Interstate '76 and Nitro. Every stock and community mission and terrain (797 and 767 files) saves back byte for byte; an edit changes only the bytes it names. New maps: a blank flat map, or a copy of any map under a new name, saved to `addon\` as a set.
- Max FPS and VSync in Graphic Detail (in the main menu's sheet and in the in-game menu), live: cap the frame rate at 30, 60, 75, 120, 144, 165 or 240 FPS, or leave it Unlimited as before, and turn VSync off or on. It limits drawing only; the simulation keeps its own rate (Play Options' Simulation Rate) and the cars are still drawn smoothly between its steps, even with the cap below that rate. To make room, the 1997 Mode row (the main menu's sheet and the in-game menu) now cycles Off, 3D Card and Software, which replaces the separate 1997 Renderer row.
- Headlights in the modern look light up the road much farther ahead (about 50 m) and wider, and the beam sits a little lower on the road, so night driving is usable without squinting (#52). Your car's lamps cast shadows as before; other cars' lamps get real light for the nearest six to the camera. The 1997 look's headlight pool is unchanged.

### Fixed
- Modern look: the army jeep's driver is matte cloth and skin. He is drawn as a part of the jeep, so he had its clear-coated paint and his uniform shone with the sky's reflection like the bodywork.
- Modern look: flat-shaded, low-poly terrain poking through the real ground near the map's edge (cliff walls, canyon rims). The distant mountain ring runs under the map as a backstop, and its coarse cells at the map's edge could stand above the modern terrain's cliff faces (which are drawn up to 28 m off the heightfield the ring followed); it is now drawn over the map only where it is under the lowest ground near each 10 m of it.
- Modern terrain: a thin light seam up a hillside or canyon wall, where the sky showed through a hairline crack along the edge between two chunks of different detail 100 to 400 m out (the 2.5 m chunks began sliding toward the 5 m shape at 190 m while the 1.25 m chunks beside them still met their edge, so the edge opened by up to 4 m on steep ground). The 2.5 m level now holds until 360 m and gives way at 450 m (it was 320 m); a test checks every chunk edge at every pair of levels, and the shading normals along it.
- The shell's MAP EDITOR entry is set in Impact like OPTIONS and EXIT (same face, slant and size) in both looks; the 1997 look drew it in the Lee face.
- The map editor's map list shows every stock map's title with its code, grouped as Your maps, Campaign, Melee arenas and Nitro in the game's own order ("Jorczak's Peak (M14)", "A New Hope (P01)"); before, only maps whose file carried a name had one. Titles come from the melee lobby's tables, the Nitro lobby's file names and Nitro's campaign list; the base campaign has no titles and reads "Mission 5 (T05)". A filter box above the list narrows it as you type.
- Variants you save of the hidden cars (helicopter, tank and the rest, with Hidden Weapons & Cars on) can be overwritten and deleted again (#53): after a restart the game took them for stock variants, refused "Overwrite" and asked you to rename, and would not delete them.
- Oil slicks and dropped fire in an online game (as host or joiner): the trail no longer vanishes a moment after you drop it (the server's drops came in with no age and no life, so they were drawn fully faded as soon as they replaced your own), and the dropper's sound is no longer cut after a few milliseconds and restarted by every drop (its loop was kept for a 60th of a second, shorter than the gap between sim ticks).
- A looping weapon sound (the oil slick, for one) is stopped from the shots alone, whatever the camera does, and a loop the distance cull had parked cannot start again by itself afterwards (#58; the exact stuck case from a look behind could not be reproduced).
- Modern look at the Far draw distance is lighter to draw (#34): the ground a kilometre or more away is drawn with coarser triangles (10 m, then 20 m between points; they were all 5 m, a few pixels each), a third to a half fewer triangles on the big maps and about a third less of what Far added to the frame time. The far ground's shapes and colours are the same to the eye.
- Modern look at the Far draw distance: holes and thin slits through the far ground (#63, #65) where the white sky showed, mostly when you looked down or sat on high ground. Pieces of the distant mountain ring were being dropped from the picture while still in view; they stay now.
- Saved trips (bookmarks, the trip's car and progress) and Nitro's campaign progress now live in a "saves" folder inside the user folder instead of filling it. Old ones are moved there on the first start, never overwritten or deleted (a clash keeps both).
- The mouse cursor stays hidden for the whole mission, in the interior (F1) view and the other views too, and whatever you bind to the mouse buttons (#49, #59). It is shown, free, in every menu (the shell, the Esc menu, the map and the notepad, Reconfigure, the end-of-mission screens), and comes back whenever a mission ends, by success, failure or Abort, or the game drops, or the window loses focus (#64).
- Oil slicks and Fire-Dropper fire show again on PCs that run the game on OpenGL (the Compatibility renderer: an older GPU, a virtual machine, Wine without Vulkan). There you slid on oil you could not see, and dropped fire, wreck flames and smoke were invisible.
- Oil, fire and caltrops lie on the ground in both looks instead of sinking into slopes and road banks in patches: a dropped trail is one unbroken streak again.
- When the host of an online game crashed or lost its connection, the game ended for everyone but the player taking over, and the mouse cursor stayed hidden afterwards, so the menus could not be used. The others now stay in the game under the new host, and the cursor always comes back when a game ends.
- CONFIGURE CHASSIS always lists every weapon mount of the car, empty ones included, so a car stripped of its weapons (or saved that way by the original game) can have them put back.
- A car's weapon slots no longer swap places between visits to CONFIGURE CHASSIS: "#1 Top" is always the same mount, and it is the first weapon in the game, as in the original.
- Online: the lobby's game info now lists the players of a game in progress whose host is behind a router (it showed none: the game is now asked through the lobby server, which also gets its ping), and the list fills in while the info is open.
- Network chat and the console's lines (kills, joins, "Say:") are back to how the original showed them: red, in the game's own Lee_____.ttf (read from your install; the old font is the fallback), sitting a third of the way down the screen at the original's spot and scaling with the window height instead of staying tiny at high resolutions.
- 1997 look at the Far draw distance: a strip of sky no longer shows between the ground and the background mountains where the map's ground ends (Trip scene 17, for one). The mountains now stand where the ground really ends.
- A joystick's button 12 and its hat are no longer the same thing (#22): on Windows and Linux the game now asks the system which of the two you pressed, so button 12 is Button 12 and the hat is HatUp/Right/Down/Left, each bindable (Button N is the stick's physical button N). Where the system cannot say, the Joystick screen's switch still decides.
- 1997 look: machine-gun tracers start at the end of the barrel. A shot just fired was drawn reaching back through the gun, so a turret turned to the side (the 7.62 turret, for one) seemed to fire from its base.
- Striped road barriers knock over when you drive through them, as in the original, instead of stopping you dead: Nitro Pack's "Allies" (#47) and two other maps. A rocket that hits one knocks it over too; machine-gun fire does not.
- Enemy cars no longer ram a building, bounce off and ram it again for good (#60). The AI could not see most buildings (the houses, barns, warehouses, fences and wrecks), so it neither steered round them nor backed out once it hit one; now it does both, as in the original.
- Helicopters fly straight at Simulation Rates above 28 Hz instead of weaving left and right, and the Army Huey in Nitro Pack's "Break In" opening no longer flies into the ridge at 40 or 60 Hz (#57). The original weaves and crashes the same way when it runs that fast; the game now steers its helicopters at 40 and 60 Hz as it did at the 28 fps it was made for.
- The speedometer works in the flying vehicles (the helicopters, balloon, UFO and Phantom), in the cockpit and on the external instruments (#61). In the original it stays at 0 in all of them.


### New
- A new in-game HUD for online games in the modern look, in the game's own oranges, browns and parchment: a panel with the time left, the race to the goal, your lap and place in a race and every flag in Capture the Flag; a kill feed with your own kills and deaths picked out; chat with each name in its team's colour; a card when you are destroyed that shows who did it and counts down to the respawn; a results panel at the end with the standings and your place; the host's invite and boot questions as pop-ups with their keys; and a proper scoreboard (hold ' or ;), ranked, grouped by team, your row lit, readable up to 4K and paging through 64 players. Same keys as before. The 1997 look keeps the original's text, now laid out across the whole wide screen as the original placed it (the console and score window at the left, the goal line under them) rather than squeezed into a 4:3 box.
- Online, when you are destroyed, a pad's A button respawns you and Y opens Reconfigure (Space and Enter still do).
- Keep Mouse In Window (Graphic Detail, in the shell and in the Esc menu; off by default): in a mission the hidden cursor cannot leave the game window, for several monitors.

## 0.5.0 (13 October 2026)

Network games use protocol 13: everyone in a game needs this version.

### New
- macOS: a universal app (Apple Silicon and Intel) beside the Windows and Linux builds.
- Finds Interstate '76 and the Nitro Pack installed through GOG Galaxy or GOG's installers by itself: from the registry and Galaxy's config, and on Linux and macOS also from a mounted Windows drive, Wine, CrossOver or Heroic.
- Your own car is predicted in network games, so it answers the keys at once; your own shots are drawn the moment you fire. Hits and damage are still the server's. I76_NET_PREDICT=0 turns prediction off.
- Bigger games for less bandwidth: up to 32 drivers on the host form and 64 on `i26-server --max`. Snapshots carry only what changed and what matters most to each player, and fit in one packet. A 16-player game needs about 1.4 Mbit/s of upload from its host; `--upload MBIT` keeps a server within a budget. Spawns spread out when there are more cars than spawn points.
- Host migration: when the host of a game hosted from the game quits or drops, the game goes on, as in the original. The lowest seat takes over ("*** NAME is now the host!") and everyone keeps their seat, car and score.
- Reconfigure while dead: press Enter after your car is destroyed to pick the car you respawn in. The server checks it as it checks a joining car.
- The host can boot players: Ctrl+1..9 on the held score window, then Ctrl+Y; `kick` on `i26-server`'s console. INVITE ONLY works on the host form and with `i26-server --invite-only`.
- Team play has all eight teams (Men In Black).
- Races and capture the flag can have no limit.
- Ctrl+T shows the names of all the players.
- Game pads: Dead Zones (Control Configuration > Calibrate) for the sticks and triggers; the sticks start with a 15 % zone, so worn sticks no longer turn the camera.
- Mouse Camera: the chase view (F2) orbits with the mouse; on by default, set in Graphic Detail.
- Modern look:
  - missile exhaust and damaged-car smoke are continuous streams;
  - the police light bar flashes red and blue and lights the road at night.
- Hidden Weapons & Cars adds the Nitro Pack's Howitzer and its two MGs with gunners.
- Some bugs of the original are fixed (deviations): your ignition hides only you from enemy turrets, not your wingman's targets (#37); AI cars no longer back away endlessly or sit still beside you (#42).

### Fixed
- Network games follow the original's rules:
  - Respawn by pressing Space after 5 s, not automatically.
  - Killing yourself or a teammate books no death; a death with no killer costs 500 points.
  - A leaving player's car blows up.
  - The scores show while ' or ; is held.
  - The original's kill, join, leave, chat and end lines, with "You win!" and the winner's camera, or "You lose, chump!".
  - The original's strict car checks.
- Online fixes:
  - players invisible to each other after the first round;
  - target keys picking nothing in free-for-all games;
  - invisible flame throwers;
  - the .45s, the ignition key and hidden aircraft;
  - the CTF flag stuttering;
  - Nitro specials and variants refused.
- Weapons, checked against the original:
  - the Cluster-Bomb scatters 8 to 11 bomblets;
  - the EZK Mortar lays its own elevation;
  - the Nitro Pack's Chemical Mortar leaves a cloud and kills the driver;
  - Caltrops blow tyres;
  - 4Get-U-Not locks on and fires;
  - Nitro's droppers drop at their own rate;
  - a shot-off weapon switches away at once;
  - incoming radar missiles warn;
  - damaged weapons jam.
- Physics and AI, checked against the original:
  - collision damage to the original's exact constants;
  - wrecks stop and stay put;
  - far cars keep the original's pace;
  - which cars use the far-away physics no longer depends on the camera;
  - the AI's path planner and look-ahead;
  - cliff falls.
- Missions: T11 winnable with turrets standing, and many script commands corrected.
- Sound: the car's own horn on G; sirens; wind in the outside views; ramp bumps; the 600 m sound cut with looping sounds coming back.
- Nitro Pack menus:
  - Play Options and the Options menu are Nitro's own, with Nitro's flags and difficulty kept apart from Interstate '76's;
  - CONFIGURE CHASSIS offers all eleven specials;
  - the restrictions form's hidden weapons are lettered like the rest.
- Shell fixes: Save/Load Bookmark, CONFIGURE CHASSIS tyres and variants, Instant Melee, team STANDINGS, and Options throwing away unsaved changes.
- T13: spare parts come out of YOU ARE UNCONSCIOUS fully repaired.
- Online:
  - a frozen-looking lobby game-info panel in the Nitro Pack;
  - Reconfigure opening on a default car that couldn't be changed;
  - "punch failed" behind some routers, and the message now says whose router stopped it;
  - Hidden Weapons & Cars games behind a router couldn't be joined;
  - custom maps with spaces in their file names;
  - the force field when bumping;
  - nitrous heard late;
  - a horn blaring after a respawn.
- Brakes in melee and network games are as strong as the original's.
- Turrets: only the gun turns, the muzzle flash and rounds leave the barrel's end, and no turret pokes into the cockpit.
- Damaged cars show their damage textures; the cockpit hood, the specials indicator and the 1997 look's night headlights in the cockpit; helicopter health bars.
- Sound:
  - heavy trucks, police (siren on the horn), tank, helicopters, balloon and UFO keep their own engine sounds and horns;
  - far engines fade as in the original;
  - far one-off sounds aren't heard.
- Skid marks in the 1997 look sit on the drawn ground.
- Modern Physics no longer takes off over crests.


## 0.4.0 (11 October 2026)

Network games use protocol 12: everyone in a game needs this version.

### New
- Internet games without port forwarding: games are listed on the community anet servers and joined through them (UDP hole punching; a strict symmetric NAT still needs a forwarded port). i26-server --anet lists a dedicated server.
- Custom maps in network games: a player without the host's map can download it on joining (Share maps on the host form, Map Downloads in Play Options).
- Chat in network games: Backspace, as in the original (rebindable as CHAT_SENDTOALL).
- Game passwords (host form, i26-server --password); locked games show a lock in the join list.
- Network melees carry on into another round after the standings: the same arena, or the next of i26-server --rotate.
- Nitro Pack: WEAPON RESTRICTIONS works, with the hidden weapons on the list, and the host checks every car that joins (i26-server --restrict).
- Hidden Weapons & Cars (Play Options, off by default): the complete weapons and vehicles the shells never offered. HADES, Cherub, Pyro-Tomic, Car-E-Racer and the tank cannons; the Tank, Huey, Cobra, Tractor, Moth tanker and Skeeter's van, the Nitro Pack's hot-air balloon, UFO and F4D Phantom, and the Armadillo.
- Draw Distance (Graphic Detail): Original clips at the original's 600 m in either look; Far, the default, draws to 20 km as before.
- Deleting saved car variants: the Nitro Pack's VARIANT list DELETE works, and the base game has one too.
- The in-car view sways with acceleration, braking and turns, as the original's.
- F10 overhead view: Page Down and Page Up zoom.
- Holding a list's arrow keeps it scrolling, in every list.
- Rolling tumbleweeds on the open dry maps in the modern look (with Vegetation on).

### Fixed
- Enemy cars not shooting the buildings they attack (T01's diner, T02's school, T06's gas station, T08's warehouse, T13's truck stop).
- AI cars driving off Seminole's bridge, and the Milk Toast semi and T10's van driving in circles.
- The target health bar staying green on a burnt car: it reads the same health as the .45 rule and the missions, as the original's.
- T12: the Komplex gate invisible, and the engine idling while Groove sleeps.
- Choosing tyres giving a car another car's wheels (variants saved earlier too).
- The garage's weight ignoring armour moved to the pool.
- The .45 lost after a regen pad.
- Scene 13 without the V-10 the original fits when Groove wakes up after Scene 12 (YOU ARE UNCONSCIOUS also repairs the car).
- Parts ordered into repair before T04 staying damaged after the dream.
- Firing a destroyed weapon droning instead of buzzing.
- Mission music looping one track: it plays on through the next tracks as the 1997 CD release did, and starts with the mission, not during loading.
- Brake lights hard to tell from lights off: the tail lamps light only with the headlights, as in the original.
- In the car: side mirrors and other parts out of place (Piranha, Cavera, Leopard, Grappler, Sovereign), the Bushmaster's weapons panel cut by its roof, the specials panel's missing housings, the binoculars' missing frame.
- Saw-toothed, striped cliffs in the 1997 look (M14, T14).
- Gaps across roads near junctions in the modern look (M06 and others).
- Slatted buildings flickering, and car shadows black and stretched, in the modern look.
- Joystick buttons 12 to 15 read as the hat: a switch on the Joystick Configuration screen.
- Nitro Pack: CONFIGURE CHASSIS missing the Chemical Mortar, 4Get-U-Not, Ball O'Flame and Caltrops; the Rattler's hotair and thetruthishere variants.
- Graphics drivers without Vulkan or D3D12 (OpenGL): shader errors, no 1997 terrain, wrong colours.
- Black screens after T05's and T06's mid-mission movies, and after a failure on the way back from a mission.
- ':' typed as ';' on some keyboards.
- Rumble stuck on in network games.
- A joiner could bring any car in the install (mission cars, the tank) into a network game: the server takes only the cars the car list offers.
- i26-server stopped by SIGTERM or SIGHUP left its anet listing up: it now shuts down as with Ctrl+C.
- The Nitro host form reopening on Combat instead of the arena chosen.

## 0.3.0 (11 October 2026)

### New
- Dedicated server (i26-server) included in the download.
- Target views (F4, F5) and the projectile camera (F11).
- 1997 Renderer: Software (Graphic Detail): the original software renderer's palette, light tables, sky and ground.
- Palette Grade (Graphic Detail): the modern look in the mission's 256 colours.
- Vegetation in the modern look (Graphic Detail, Low by default): procedural plants for every region of the trip (South Plains, south-eastern New Mexico sand country, Pecos, Trans-Pecos, Davis Mountains) and the melee arenas, other maps by their ground; bunchgrass tufts in a patchy sward, shaded by their own foliage, parting around cars, grey in T04's dream.
- M15 (and Nitro's Lot of Asphalt) is a parking lot in the modern look, its markings where the original's are.
- A drawn sun in the modern look's sky, behind the clouds.
- The modern look's distant mountains sit as low as the original's on high maps.
- Joysticks that are not game pads (USB analog sticks) work, with a Joystick Configuration screen (Calibrate).
- Tyre sounds: skids, squeals in hard turns, rolling on dirt, sand and gravel, worn brakes grinding.
- The F9 mission list names the melee arenas and scenarios.

### Fixed
- Cars thrown off the road over crests at the default 20 Hz Simulation Rate (T09).
- Mortars (and other lobbed rounds) flying far too far.
- Flame weapons' damage: as the original's, less up close, and the same in every view.
- AI turrets and helicopters aiming at you with your engine off.
- T11 ground turret that could not be hit.
- The Rattler's helicopter variant (Nitro) pointing at a missing body.
- Damage from the T04 dream carried into T05.
- Save Bookmark doing nothing without a name, and Enter not saving.
- The 1997 look washed out: it now has the Glide driver's default gamma, and Monitor Brightness uses the original's curves.
- Night missions in the 1997 look: headlight pools, car lights on objects and shadows, lit lamps and windows, black shadows, horizon panels sunk as the original's.
- Car lamps drawn from behind in the 1997 look.
- The rear-view mirror cut off in the instruments-only view (F1).
- Wrecks and flying car parts see-through from inside in the modern look.
- A car's underside looking see-through in the modern look.
- Burning wrecks (M14, T15 and others) only smoking, without their flames, in the modern look.
- Plants, smoke and flames showing inside the cab in the cockpit view.
- No way out of a network game: Esc opens the menu (the game keeps running), with Leave Game.
- The dash's gear selector not moving in network games (network protocol 10: everyone needs the new build).
- Nitro scenarios not opening on a fresh install (no nitscar.def).
- Missions start in the cockpit, as in the original.
- Difficulty defaults to Wimp, as the original shipped (a saved choice stays).
- The radar beam points at your target, and target brackets show in the mirror, as in the original.

## 0.2.0 (9 October 2026)

### New
- Game pad support: driving, right-stick camera, cockpit free look, menu navigation.
- Pad: Start opens the game menu, B skips cut-scenes, hot-plugging works.
- Force feedback (Play Options).
- Controller binding screen (Calibrate in Control Configuration).
- Control Configuration in the in-mission Esc menu.
- WASD steering by default; Toggle Weapon Cam on F, Ignition on I.
- Simulation Rate setting (Play Options), 20 Hz by default.
- Modern Physics follows the Simulation Rate.
- Checkpoints (Play Options, off by default).
- 2D dash (F1 twice).
- F2 twice toggles the outside instruments.
- The original's cheat codes.
- Skid marks.
- Custom maps: Nitro maps and Interstate '76 `.lvl` maps.
- Custom car variants in network games.
- Game chooser when both games are available; Switch Game in Options.
- Game data unpacked from GOG offline installers.
- F9 mission list starts you in a strong car.
- Build version shown in the menu.
- I-26 icon.

### Fixed
- T05 and other bridges can be driven across.
- T07 roadblock gates slide open and shut.
- AI cars swerving off the road (Taurus in T03).
- DrRadar missiles not homing.
- Trip salvage amounts and damaged parts from wrecks.
- A second tyre of the same size replacing the first.
- Car launched without an engine.
- Bookmarks not restoring the car and parts.
- Napalm and flame weapons' plume length.
- Cockpit radar target blip drawn black.
- Cockpit glances moving the chase camera.
- Car hidden in cut-scenes when in the cockpit view.
- Hangs with Bluetooth pads.
- Stray wheel on helicopters; blurred rotor discs in the modern look.
- Tanker truck missing its middle wheels.
- AI brake lights strobing.
- 1px line in the cockpit view.
- Smoke over the Fire-Dropper's flames.
- Control Configuration text overlap and flicker.
- Auto Reverse with a pad.
- Custom maps that fail to build now return to the form with a note.

### Faster
- Missions load in half the time or less, and faster again on a second load.
- Missions started in the 1997 look don't build the modern terrain until you switch.
- Terrain Detail and Shadow Detail default to Low.
