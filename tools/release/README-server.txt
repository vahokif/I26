I26 dedicated server @BUILD@
============================

This is the dedicated server of I26, a fan-made reimplementation of
Interstate '76 (Activision, 1997) and its Nitro Pack, for 32-bit ARM Linux
(armv7, hard-float: a Raspberry Pi 2/3, or a Pi on a 32-bit OS). It has no
game window and no Godot: it runs a network melee, race, capture-the-flag or
co-op mission for players on the full I26 game (Windows, Linux, macOS).
.NET is included; nothing needs installing. For 64-bit ARM use the full
linux-arm64 zip instead, it has the same server.

It ships NO game data. It needs your own legally owned Interstate '76 or
Nitro Pack files (the GOG release works), read-only, on the machine it runs on.
I26 is not affiliated with, endorsed by or connected to Activision or Microsoft.

Run (from a terminal, in this folder i26-server-@VERSION@)
---------------------------------------------------------
    ./i26-server --install /path/to/i76 --arena m05 --port 7676 --ai 3

It finds the game the same way I26 does (and says which one it chose; with
both, Interstate '76 unless you add --game nitro): --install "path", the
I76_INSTALL environment variable, an "i76" / "nitro" folder here or in the
parent folder, a GOG offline installer (setup_interstate76_*.exe) put next to
it, which it unpacks once into "i76" / "nitro" (or ~/.local/share/I26 if this
folder is read-only). Copy the game folder over from another machine if need be.
It listens on UDP port 7676 (open it in your firewall; --port changes it).
Type a line to say it in the game's chat; Ctrl+C stops it. ./i26-server --help
lists the options (players, teams, score and time limits, races, capture the
flag, co-op missions).

If it does not start: the files must stay together in this folder, and
i26-server must be executable (chmod +x i26-server). A 32-bit ARM board is
slow: keep --ai and the player count small.

Credits and licences are in THIRD-PARTY-NOTICES.txt.
