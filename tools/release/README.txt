I26 @BUILD@
===========================

I26 is a fan-made reimplementation of Interstate '76 (Activision, 1997) and
its Nitro Pack, built on the Godot engine. It plays the original game from
your own copy of the files, with a modern renderer and, if you like, the 1997
look.

It ships NO game data. You need a legally owned Interstate '76 or Interstate
'76 Nitro Pack (the GOG release works). I26 is not affiliated with, endorsed
by or connected to Activision or Microsoft; Interstate '76 is their trademark.

Install
-------
Unpack this whole folder (i26-@VERSION@) anywhere and run i26.exe (Windows,
64-bit), i26.x86_64 (Linux, 64-bit PC), i26.arm64 (Linux, 64-bit ARM) or I26.app (macOS, see below) from inside it. Nothing needs installing;
.NET is included.

Which zip is for which machine:
  i26-VERSION-windows.zip           Windows, 64-bit Intel/AMD
  i26-VERSION-linux.zip             Linux, 64-bit Intel/AMD (x86_64)
  i26-VERSION-linux-arm64.zip       Linux, 64-bit ARM (aarch64): a Raspberry Pi 4/5, an ARM laptop, Asahi.
                                    It needs a Vulkan or OpenGL driver; its textures are ETC2/ASTC, which ARM GPUs take.
  i26-VERSION-macos.zip             macOS, Apple silicon and Intel

Give I26 the game one of these ways:

- Installed GOG game (including GOG Galaxy): I26 finds it by itself, on
  Windows, Linux (a Windows drive mounted, a Wine prefix, Heroic) and macOS
  (CrossOver, mounted volumes): it reads GOG's registry entries and Galaxy's
  config, and only trusts a folder holding the game's goggame-*.info file.
  Nothing is written there. Otherwise EITHER copy your game folder into
  this folder as "i76" (and "nitro" for the Nitro Pack); OR unpack this folder
  into your game's folder (only that game is found then).
- GOG offline installers: put setup_interstate76_*.exe (or
  setup_interstate76_nitro_pack_*.exe) next to i26.exe, or in the folder above
  it. I26 unpacks it into "i76" / "nitro" itself, once (about 470 MB for
  Interstate '76, 290 MB for the Nitro Pack). If this folder is not writable
  or has no room, it goes into your local user folder instead (Windows
  %LOCALAPPDATA%\I26, Linux ~/.local/share/I26, macOS
  ~/Library/Application Support/I26), also as "i76" / "nitro". I26
  never unpacks into a folder holding something else. The GOG Galaxy
  downloader stub won't do: it holds no game.
- Anywhere else: a shortcut with  -- --install "C:\path\to\game"
- Updating I26: move your "i76" and "nitro" folders into the new version's
  folder (or keep them in the local user folder, which every version checks).

I26 remembers no location: each run it looks at --install / I76_INSTALL, this
folder and the one above, "i76" and "nitro" here, then in the local user
folder, then the GOG Galaxy / GOG installs on the machine. If it finds nothing it says so and lists these places.

Which game starts: I26 looks for installs and GOG installers next to it, in
the parent folder and in your local user folder. With one game it starts at once
(unpacking the installer first if that is all there is). With both Interstate
'76 and the Nitro Pack it shows their two logos: click one, or Left/Right and
Enter (the pad's D-pad and A); Esc quits. A game that is still an installer
is unpacked when you pick it. The main menu's Esc options have "Switch Game"
to come back to the choice.

macOS
-----
GOG sells only the Windows game, but its offline installer files still hold
the game, and I26 can unpack them on a Mac. In your library on gog.com,
download Interstate '76 (and the Nitro Pack) as offline backup installers:
the setup_interstate76_*.exe, with its .bin parts if it has any. Put them all
next to I26.app (in this folder) and start I26: it unpacks them into "i76" / "nitro"
beside the app, once. Or copy the folder of an existing install there as "i76"
(or "nitro"). Everything above about the folders applies, with I26.app's own
folder in place of the executable's. I26 never writes inside I26.app.

I26.app runs on Apple silicon and Intel Macs (macOS 11 and later on Apple
silicon, 10.15 on Intel). It is not notarized (I26 has no Apple developer
account), so macOS stops it the first time. Either right-click I26.app, choose
Open, and Open again; or, in a Terminal in this folder:

    xattr -dr com.apple.quarantine .

The second is the safer: an app opened while it still has the flag runs from a
temporary copy elsewhere (App Translocation), where "i76" and "nitro" beside it
are not found.

The dedicated server is not part of the macOS build.

Keep the files of this folder together: the .pck file and the data_* folders
belong to the executable.

Settings and saves
------------------
Your settings, key bindings, saved trips and screenshots live in your user
folder, never in the game folder (bookmarks and the trip's progress are in its
"saves" folder; older versions left them loose in the folder itself, and I26
moves them there the first time it starts):
  Windows: %APPDATA%\I26\
  Linux:   ~/.local/share/I26/
  macOS:   ~/Library/Application Support/I26/
Delete that folder to start from scratch.

Dedicated server
----------------
(Windows and Linux builds, arm64 too.) i26-server (i26-server.exe on Windows) runs a network melee without the game
window, for a LAN or the internet. Run it from a terminal, in this folder:

    i26-server --arena m05 --port 7676 --ai 3

It finds the game the same way I26 does (and says which one it chose; with
both, Interstate '76 unless you add --game nitro), or take --install "path".
It listens on UDP port 7676 (open it in your firewall; --port changes it).
Type a line to say it in the game's chat; Ctrl+C stops it. i26-server --help
lists the options (players, teams, score and time limits, races, capture the
flag).

Subtitles generator
-------------------
(Windows and Linux builds, arm64 too.) i26-subtitles (i26-subtitles.exe on Windows) generates
speech-to-text subtitles directly from the original cutscenes (.SMK) and mission dialogues (.WAV):

    i26-subtitles

It finds your game install automatically, downloads the speech recognition model on first run,
and saves subtitles directly into your user subtitles directory. Run i26-subtitles --help
to view available options (choosing game, model size, or filters).

Credits and licences of the parts that ship with I26 are in
THIRD-PARTY-NOTICES.txt.
