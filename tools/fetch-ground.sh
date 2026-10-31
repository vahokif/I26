#!/usr/bin/env bash
# Fetch the third-party ground materials the modern look's terrain uses.
#
#   tools/fetch-ground.sh            (I76_GROUND_RES=2K for the 2K sets; 1K by default)
#
# They are ambientCG's PBR material scans, released under CC0:
# https://ambientcg.com (Ground102, Ground031, Ground079S, Rock061, and
# Asphalt015 for painted grounds such as M15's parking lot). From each set's
# zip only the maps the terrain shader packs are kept: colour, displacement,
# OpenGL normal, roughness and ambient occlusion (where the set has one). They land in
# game/ground/, which is not committed. A release export imports them like
# any texture (run `godot-mono --headless --path game --import` after
# fetching); a development run reads the files directly. Without them the
# modern terrain falls back to its procedural ground (GroundNoise).
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
dest=$here/game/ground
res=${I76_GROUND_RES:-1K}
# the sets TerrainMaterials.cs reads, by ambientCG asset id
sets=(Ground102 Ground031 Ground079S Rock061 Asphalt015)
maps=(Color Displacement NormalGL Roughness)   # and AmbientOcclusion, which Asphalt015 lacks
mkdir -p "$dest"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
for id in "${sets[@]}"; do
    have=1
    for m in "${maps[@]}"; do [ -s "$dest/${id}_$m.jpg" ] || have=0; done
    if [ $have = 1 ]; then echo "have $id"; continue; fi
    echo "fetching $id ($res)"
    curl -fsSL --retry 4 --retry-all-errors -o "$tmp/$id.zip" "https://ambientcg.com/get?file=${id}_${res}-JPG.zip"
    python3 - "$tmp/$id.zip" "$id" "$res" "$dest" "${maps[@]}" AmbientOcclusion <<'PY'
import sys, zipfile
z, ident, res, dest, *maps = sys.argv[1:]
zf = zipfile.ZipFile(z)
names = set(zf.namelist())
for m in maps:
    name = f"{ident}_{res}-JPG_{m}.jpg"
    if name not in names and m == "AmbientOcclusion":
        continue
    with zf.open(name) as src, open(f"{dest}/{ident}_{m}.jpg", "wb") as out:
        out.write(src.read())
PY
done
cat > "$dest/SOURCE.txt" <<TXT
ambientCG PBR materials (CC0): ${sets[*]}
https://ambientcg.com
Fetched by tools/fetch-ground.sh; not part of the repository.
TXT
echo "ground materials in $dest"
