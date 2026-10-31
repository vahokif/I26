#!/usr/bin/env bash
# The CB voice chat end to end on one machine: a listen host ("Ann") feeds a WAV in as her microphone (I76_CB_INPUT) once a second
# player is in (both games quit once the talk is through: I76_CB_QUIT=1); the client ("Bob") records what the radio plays him (I76_CB_RECORD). No sound hardware is used (Dummy audio driver).
#   tools/cb-test.sh OUTDIR [INPUT.wav] [seconds]     (I76_INSTALL set; each game runs through tools/shot.sh)
# Writes OUTDIR/{host,join}.log and OUTDIR/heard-by-bob.wav (what Bob's game played, in the radio's voice), and checks the logs.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "${1:?OUTDIR}")
input=$(realpath "${2:-$HOME/.cache/i26-cb/voice-in.wav}")
seconds=${3:-70}
port=${I76_CB_PORT:-7711}
mkdir -p "$out"
cache=${XDG_CACHE_HOME:-$HOME/.cache}/i76-shot
rm -f "$out/heard-by-bob.wav"
I76_CB_SHOT=${I76_CB_SHOT:-} I76_CB_QUIT=1 I76_CB_INPUT=$input I76_CB_AT=${I76_CB_AT:-26} I76_SHOT_DATA=$cache/cb-host I76_SHOT_SIZE=${I76_SHOT_SIZE:-640x360} I76_NET_TEST=2,$seconds \
    "$here/tools/shot.sh" "$out/host.png" --host-listen --arena m05 --name Ann --port "$port" --game-name "CB test" > "$out/host.log" 2>&1 & hp=$!
sleep 8
I76_CB_QUIT=1 I76_CB_RECORD=$out/heard-by-bob.wav I76_SHOT_DATA=$cache/cb-join I76_SHOT_SIZE=${I76_SHOT_SIZE:-640x360} I76_NET_TEST=2,$seconds \
    "$here/tools/shot.sh" "$out/join.png" --connect "127.0.0.1:$port" --name Bob > "$out/join.log" 2>&1 & jp=$!
rc=0
wait $hp || rc=$?
wait $jp || rc=$?
check() { if grep -q "$2" "$out/$1.log"; then echo "ok   $1: $3"; else echo "FAIL $1: $3 (no \"$2\" in $1.log)"; rc=1; fi; }
check host "\[cb\] test input" "Ann loaded the WAV"
check host "\[cb\] transmitting" "Ann went on the air"
check host "\[cb\] test input sent" "Ann sent it all"
check join "\[cb\] recorded" "Bob's radio played it"
[ -s "$out/heard-by-bob.wav" ] && echo "     $out/heard-by-bob.wav" || { echo "FAIL no heard-by-bob.wav"; rc=1; }
exit $rc
