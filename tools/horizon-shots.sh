#!/usr/bin/env bash
# The distant mountains of one map, in both looks, from four places and four headings, for looking at (the headless
# check is tests/I76.Game.Tests/HorizonEverywhereTests.cs). Cockpit view with the car dropped at each spot.
#   tools/horizon-shots.sh MAP OUTDIR [x,z ...]     e.g. tools/horizon-shots.sh t01 ~/.cache/i26-horizon/t01
# The spots default to the car's start. Needs I76_INSTALL like tools/shot.sh. Look for: mountains standing over the
# ground's skyline in every picture (the modern look's ring, the 1997 look's panels), no jagged teeth along their foot, the
# same mountains from spots a few metres apart.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
map=${1:?map name}; out=${2:?output folder}; shift 2
mkdir -p "$out"
spots=("$@"); [ ${#spots[@]} -gt 0 ] || spots=("")
i=0
for spot in "${spots[@]}"; do
  for yaw in 0 90 180 270; do
    for look in modern 1997; do
      args=(--mission "$map" --melee --drive)
      [ "$look" = 1997 ] && args+=(--1997)
      [ -n "$spot" ] && export I76_SPAWN_AT="${spot},$yaw" || unset I76_SPAWN_AT
      "$here/shot.sh" "$out/${map}_${i}_${yaw}_${look}.png" "${args[@]}" >/dev/null
    done
  done
  i=$((i + 1))
done
echo "pictures in $out"
