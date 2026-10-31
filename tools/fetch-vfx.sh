#!/usr/bin/env bash
# Fetch the third-party explosion and smoke flipbooks the modern look uses.
#
#   tools/fetch-vfx.sh
#
# They are Unity Labs' free VFX image sequences, released under CC0:
# https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks
# They land in game/vfx/, which is not committed. A release export imports
# them like any texture (project.godot's importer defaults: VRAM-compressed,
# mipmapped); a development run without an import loads the files directly.
# Without them the game falls back to the original XDFC sprites.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
dest=$here/game/vfx
base=https://unity3d.com/files/labs/downloads/vfx/assets01
# effect name -> the atlas inside its flipbook zip
sets=(
    "Explosion02HD:Explosion02HD_5x5.tga"
    "Explosion01:Explosion01_5x5.tga"
    "FireBall02:FireBall02_8x8.tga"
    "WispySmoke01:WispySmoke01_8x8.tga"
)
mkdir -p "$dest"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
for s in "${sets[@]}"; do
    name=${s%%:*} file=${s#*:}
    if [ -s "$dest/$file" ]; then echo "have $file"; continue; fi
    echo "fetching $name"
    curl -fsSL -o "$tmp/$name.zip" "$base/$name/$name-flipbooks.zip"
    python3 - "$tmp/$name.zip" "$file" "$dest" <<'PY'
import sys, zipfile
z, member, dest = sys.argv[1:]
zipfile.ZipFile(z).extract(member, dest)
PY
done
cat > "$dest/SOURCE.txt" <<TXT
Unity Labs free VFX image sequences (CC0)
https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks
Fetched by tools/fetch-vfx.sh; not part of the repository.
TXT
echo "flipbooks in $dest"
