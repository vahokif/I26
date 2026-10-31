#!/usr/bin/env bash
# MULTI MELEE end to end on one machine, through the shell as a player would go:
#   one game hosts: MELEE > MULTI MELEE > HOST > IPX (the LAN) > BROADCAST GAME, a listen server in its process;
#   a second finds it by LAN discovery: MELEE > MULTI MELEE > JOIN > IPX, waits until the roster lists it,
#   picks it and presses JOIN GAME.
# Both play until the host's melee ends on a time limit, then each is back in the shell on the STANDINGS.
#   tools/lan-test.sh OUTDIR [seconds]     (I76_INSTALL set; each game runs through tools/shot.sh)
# Writes OUTDIR/{host,join}.log, {host,join}_game.png (each view once both are in) and {host,join}.png (the
# STANDINGS), and checks the logs: the joiner found the host's game, joined it, and both saw two players.
# I76_LAN_NAV, I76_LAN_HOST_IN, I76_LAN_JOIN_IN replace the click scripts (the Nitro Pack's menu sits elsewhere; see docs/multiplayer.md).
# Extra arguments for both games come from I76_LAN_ARGS (say --1997).
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "${1:?OUTDIR}")
seconds=${2:-40}
mkdir -p "$out"
cache=${XDG_CACHE_HOME:-$HOME/.cache}/i76-shot
user() { # user DIR DRIVER MODEL VARIANT: a scratch user:// whose melee form starts with that driver and car
    local d="$cache/lan-$1/I26"
    mkdir -p "$d"
    rm -f "$d/melee.cfg" "$d/internet.lst"
    printf '[melee]\n\narea=4\nmodel=%s\nvariant=%s\ndrivers=4\ngame="LAN Test"\ndriver="%s"\n' "$3" "$4" "$2" > "$d/melee.cfg"
    echo "$cache/lan-$1"
}
nav=${I76_LAN_NAV:-"click:447,311@8;click:395,351@12"}
# HOST, IPX, BROADCAST GAME
host_in=${I76_LAN_HOST_IN:-"$nav;click:367,379@16;click:343,406@20;click:300,462@26"}
# JOIN, IPX, hold until a game is listed, pick the first row, JOIN GAME
join_in=${I76_LAN_JOIN_IN:-"$nav;click:417,379@16;click:343,406@20;wait:games=1@24;click:100,155@26;click:300,462@28"}
run() { # run NAME DATA INPUT
    I76_SHOT_DATA=$2 I76_SHOT_FRAMES=${I76_LAN_FRAMES:-120} I76_SHELL_INPUT=$3 I76_NET_TEST=${I76_LAN_NET_TEST:-2,$seconds} \
        "$here/tools/shot.sh" "$out/$1.png" ${I76_LAN_ARGS:-} > "$out/$1.log" 2>&1
}
rm -f "$out"/*.png
run host "$(user host Taurus 20 1)" "$host_in" & hp=$!
run join "$(user join Jade 4 ${I76_LAN_JOIN_VARIANT:-2})" "$join_in" & jp=$!
rc=0
wait $hp || rc=$?
wait $jp || rc=$?
check() { if grep -q "$2" "$out/$1.log"; then echo "ok   $1: $3"; else echo "FAIL $1: $3 (no \"$2\" in $1.log)"; rc=1; fi; }
check host "listen server: m05" "hosted Slick Track on its own listen server"
check host "\[server\] join: Jade" "the joiner came in"
check join "network: joining" "found the game and joined it"
check join "joined as slot 1" "was given the second seat"
check host "net test: 2 players" "saw both players"
check join "net test: 2 players" "saw both players"
check host "back to the shell (2 standings" "came back to its STANDINGS"
check join "back to the shell (2 standings" "came back to its STANDINGS"
for f in host_game host join_game join; do [ -s "$out/$f.png" ] && echo "     $out/$f.png" || { echo "FAIL no $f.png"; rc=1; }; done
exit $rc
