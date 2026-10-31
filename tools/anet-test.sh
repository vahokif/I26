#!/usr/bin/env bash
# MULTI MELEE over an anet game server, end to end on one machine through the shell, against a LOCAL server only
# (never a community one): the anet server from the LGPL source runs on 127.0.0.1:21157 (docs/multiplayer.md,
# "Testing against the real server"; I76_ANET_SERVER names its binary, built with the ANET_BIND change).
#   one game hosts: MELEE > MULTI MELEE > HOST > INTERNET > the book's "Local anet" (127.0.0.1) > DONE > BROADCAST GAME,
#     a listen server listed on the local server;
#   a second opens JOIN > INTERNET > the same entry, is captured once its roster lists the game (roster.png), and its
#     game popup (popup.png);
#   a third joins it from the roster through the server's path (on one machine, with no NAT between, the host answers the
#     joiner's first probe before the introduction; the NAT cases are tests/I76.Net.Tests/Anet).
#   tools/anet-test.sh OUTDIR [seconds]     (I76_INSTALL set; each game runs through tools/shot.sh)
# Extra arguments for every game come from I76_ANET_ARGS (say --1997). I76_ANET_NAV, I76_ANET_HOST_IN,
# I76_ANET_ROSTER_IN and I76_ANET_JOIN_IN replace the click scripts, I76_ANET_BOOK the address book's and I76_ANET_ARROW
# the roster's first arrow (the Nitro Pack's menu sits elsewhere: docs/multiplayer.md "Lobby servers (anet)").
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "${1:?OUTDIR}")
seconds=${2:-40}
server=${I76_ANET_SERVER:?set I76_ANET_SERVER to the anet server binary (built from the LGPL source)}
mkdir -p "$out"
cache=${XDG_CACHE_HOME:-$HOME/.cache}/i76-shot
if ss -uln | grep -q '127.0.0.1:21157\|0.0.0.0:21157\|\*:21157'; then echo "UDP 21157 is taken"; exit 1; fi

# the server, bound to loopback, in its own scratch folder
srv="$cache/anet-server"
mkdir -p "$srv"
printf '1234 Test Room\n' > "$srv/rooms.txt"; echo "127.0.0.1 localhost" > "$srv/servers.txt"; : > "$srv/apps.txt"
(cd "$srv" && ANET_BIND=127.0.0.1 UUDP_HOSTNAME=127.0.0.1 ANET_SBDPORT=21158 exec "$server" -p=21157 -R=rooms.txt -H=servers.txt -A=apps.txt \
    > "$out/anet-server.log" 2>&1 < /dev/null) &
spid=$!
trap 'kill $spid 2>/dev/null || true' EXIT
sleep 1

user() { # user DIR DRIVER MODEL VARIANT ADDRESS: a scratch user:// with that melee driver and car, and an address book of the local server
    # (the host's entry is "127.0.0.1": listed there; the joiners' "127.0.0.1:21157": only the server's list, not a game on this machine)
    local d="$cache/anet-$1/I26"
    mkdir -p "$d"
    rm -f "$d/melee.cfg" "$d/internet.lst"
    printf '[melee]\n\narea=4\nmodel=%s\nvariant=%s\ndrivers=4\ngame="Anet Test"\ndriver="%s"\n' "$3" "$4" "$2" > "$d/melee.cfg"
    python3 - "$d/internet.lst" "$5" <<'EOF'
import struct, sys
e = [("Local anet", sys.argv[2])]
with open(sys.argv[1], "wb") as f:
    f.write(struct.pack("<I", len(e)))
    for n, a in e: f.write(n.encode().ljust(32, b"\0") + a.encode().ljust(64, b"\0"))
EOF
    echo "$cache/anet-$1"
}
nav=${I76_ANET_NAV:-"click:447,311@8;click:395,351@12"}
book=${I76_ANET_BOOK:-"click:284,230@24;click:228,417@26"}
host_in=${I76_ANET_HOST_IN:-"$nav;click:367,379@16;click:407,406@20;$book;click:300,462@30"}
roster_in=${I76_ANET_ROSTER_IN:-"$nav;click:417,379@16;click:407,406@20;$book;wait:games=1@28"}
join_in=${I76_ANET_JOIN_IN:-"$nav;click:417,379@16;click:407,406@20;$book;wait:games=1@28;click:100,155@32;click:300,462@34"}
run() { # run NAME DATA INPUT FRAMES [env...]
    local name=$1 data=$2 input=$3 frames=$4; shift 4
    env "$@" I76_SHOT_DATA=$data I76_SHOT_FRAMES=$frames I76_SHELL_INPUT=$input \
        "$here/tools/shot.sh" "$out/$name.png" ${I76_ANET_ARGS:-} > "$out/$name.log" 2>&1
}
rm -f "$out"/*.png
run host "$(user host Taurus 20 1 127.0.0.1)" "$host_in" 120 I76_NET_TEST=2,$seconds & hp=$!
# the roster once it lists the game (wait: holds the frames until then), and the game's popup from its arrow
run roster "$(user roster Skeeter 4 1 127.0.0.1:21157)" "$roster_in" 36
run popup "$(user roster Skeeter 4 1 127.0.0.1:21157)" "${roster_in};click:${I76_ANET_ARROW:-212,155}@32" 38
run join "$(user join Jade 4 ${I76_ANET_JOIN_VARIANT:-2} 127.0.0.1:21157)" "$join_in" 120 I76_NET_TEST=2,$seconds & jp=$!
rc=0
wait $hp || rc=$?
wait $jp || rc=$?
check() { if grep -q "$2" "$out/$1.log"; then echo "ok   $1: $3"; else echo "FAIL $1: $3 (no \"$2\" in $1.log)"; rc=1; fi; }
check host "listing the game on 127.0.0.1" "listed its listen server on the local anet server"
check host "Listed on 127.0.0.1" "the server took the listing"
check join 'joining "Anet Test" through 127.0.0.1:21157' "joined the listed game through the server"
check join "the host answered from 127.0.0.1" "was answered by the host (on one machine with no NAT it answers the first probe, before the server's introduction)"
check join "joined as slot 1" "was given the second seat"
check host "net test: 2 players" "saw both players"
check join "net test: 2 players" "saw both players"
check join "back to the shell (2 standings" "came back to its STANDINGS"
for f in roster popup host_game join_game join; do [ -s "$out/$f.png" ] && echo "     $out/$f.png" || { echo "FAIL no $f.png"; rc=1; }; done
exit $rc
