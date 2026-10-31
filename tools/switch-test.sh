#!/usr/bin/env bash
# The guard for "Switch Game" in-process: base menu -> base mission -> switch -> Nitro menu -> Nitro mission -> switch -> base menu,
# checking the install in use and that no static state of the other game is left (game/Scripts/Boot/SwitchProbe.cs).
#   I76_INSTALL=/path/to/i76 I76_NITRO_INSTALL=/path/to/nitro tools/switch-test.sh [OUTDIR]
# Captures (chooser after a switch, each game's menu and mission) go to OUTDIR (default ~/.cache/i76-switch). Uses scratch user data
# with symlinks to the installs; nothing is written into them. Prints "switch-test: PASS" and exits 0, or FAIL.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=${1:-$HOME/.cache/i76-switch}
data=$out/data
mkdir -p "$data/I26"
ln -sfn "${I76_INSTALL:?set I76_INSTALL}" "$data/I26/i76"
ln -sfn "${I76_NITRO_INSTALL:?set I76_NITRO_INSTALL}" "$data/I26/nitro"
log=$out/switch-test.log
I76_INSTALL=none I76_SHOT_DATA=$data I76_SWITCH_TEST=$out I76_SHOT_FRAMES=100000 \
    timeout 600 "$here/tools/shot.sh" "$out/unused.png" --choose-game >"$log" 2>&1 || true
grep "switch-test:" "$log"
grep -q "switch-test: PASS" "$log"
