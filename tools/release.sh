#!/usr/bin/env bash
# Build the I26 release zips: Windows x64, Linux x86_64, Linux arm64 and macOS (universal), one zip each, and a
# dedicated-server-only zip for 32-bit ARM Linux (a Raspberry Pi and the like), opt-in: I26_ARMV7=1, unverified on hardware (see below).
#
#   tools/release.sh VERSION          e.g. tools/release.sh 1.0   ->  dist/i26-1.0-windows.zip, -linux.zip, -linux-arm64.zip,
#                                                                     -macos.zip and dist/i26-server-1.0-linux-armv7.zip
#
# Each zip has one top-level folder, i26-VERSION/, to unpack into the player's
# Interstate '76 folder: the executable (i26.exe or i26.x86_64), i26.pck, its
# .NET data folder (self-contained: the player needs no .NET), the dedicated server (launcher i26-server[.exe]; its few files sit in the data folder, sharing the runtime), README.txt and
# THIRD-PARTY-NOTICES.txt. No game data.
#
# The arm64 zip (executable i26.arm64, Godot's name for it) is the Linux game and server again, for aarch64 machines
# (a Pi 4/5, an ARM laptop, Asahi). ARM GPUs generally lack BC7/S3TC, so its pck carries the ETC2/ASTC texture
# variants (the same project setting as the macOS arm64 half). The runtime pack is NuGet's linux-arm64. The terrain's
# material pack (ground/pack.bin) holds BPTC and ASTC variants and the game takes the one its GPU has (TerrainMaterials.GpuVariant),
# so ARM GPUs get ASTC; the road layers (MissionBuilder.CompressRoadLayers) compress in editor builds only and stay RGBA8 in exports.
# The armv7 zip (only with I26_ARMV7=1; it crashes under qemu-user, unverified on hardware) is the server alone (src/I76.Server + tools/I76.Server, no Godot), self-contained for linux-arm
# (32-bit, hard-float), with its own README and the notices: i26-server-VERSION/ holds the launcher and the runtime.
#
# Needs network the first time: the free assets (tools/fetch-vfx.sh,
# tools/fetch-ground.sh) and the .NET runtime packs the self-contained
# publish takes from NuGet (win-x64, linux-x64, linux-arm64, linux-arm for the server, osx-x64, osx-arm64).
#
# Export templates: Godot needs the mono templates of exactly the editor's
# version. The flake builds them as `.#export-templates` (nixpkgs'
# godotPackages.export-templates-mono-bin, a fixed-output download of the
# official templates, hash-checked by nix, version = the editor's 4.7.2).
# They are linked into a private XDG_DATA_HOME under the cache folder
# ($I26_CACHE, default ~/.cache/i26-release), where Godot looks, so the
# user's own editor settings and templates are not touched.
#
# The boot splash is res://Assets/boot_splash.png (ours: black, a line of text);
# nothing under user:// may be packed, which is checked by listing the .pck.
# The version is written into project.godot only for the export (restored on exit).
set -euo pipefail
ver=${1:?usage: tools/release.sh VERSION}
[[ $ver =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || { echo "bad version: $ver" >&2; exit 2; }
here=$(cd "$(dirname "$0")/.." && pwd)
cache=${I26_CACHE:-$HOME/.cache/i26-release}
name=i26-$ver
nixf=(nix --extra-experimental-features 'nix-command flakes')
cd "$here"

# 0. code the export's older .NET SDK (8.0.1xx) cannot build though nix's newer one does: a collection expression handed to
# string.Split is ambiguous there (char[] or string overload); fail here rather than after the imports
if grep -rnE --include='*.cs' '\.Split\(\[' src game/Scripts tools 2>/dev/null | grep -v '/obj/'; then
    echo "Split([...]) above: the export SDK finds it ambiguous; write Split(new[] { ... }, ...)" >&2; exit 1
fi

# 1. the free assets, imported, and the templates
tools/fetch-vfx.sh
tools/fetch-ground.sh
tpl=$("${nixf[@]}" build --no-link --print-out-paths .#export-templates)/share/godot/export_templates
mkdir -p "$cache/xdg/godot/export_templates"
for d in "$tpl"/*; do ln -sfn "$d" "$cache/xdg/godot/export_templates/$(basename "$d")"; done
export XDG_DATA_HOME=$cache/xdg

# The nix dotnet SDK carries its own linux-x64 runtime pack, built against nix's glibc (and RUNPATHed into
# /nix/store): a self-contained Linux build that took it would run nowhere else. Asking for another runtime
# patch version than the SDK's makes the publish take the official pack from NuGet (needs glibc 2.16), and
# the same for win-x64. MSBuild reads this environment variable as a property; Godot's own publish sees it.
export RuntimeFrameworkVersion=${I26_DOTNET_RUNTIME:-8.0.30}

# 2. stamp the version for this export only: "0.1.0 (3e3e5b5)", "-dirty" after the hash if tracked files have changes
# (the menu's corner, the log's first line and the README's first line show it, so a tester's build can be told apart)
hash=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)
[ -z "$(git status --porcelain --untracked-files=no 2>/dev/null)" ] || hash=$hash-dirty
stamp="$ver ($hash)"
cp game/project.godot "$cache/project.godot.orig"
cp game/export_presets.cfg "$cache/export_presets.cfg.orig"
trap 'cp "$cache/project.godot.orig" "$here/game/project.godot"; cp "$cache/export_presets.cfg.orig" "$here/game/export_presets.cfg"' EXIT
sed -i "s|^config/name=.*|&\nconfig/version=\"$stamp\"|" game/project.godot
grep -qF "config/version=\"$stamp\"" game/project.godot
# the Windows exe's version resource wants four numbers: 0.1.0 -> 0.1.0.0
winver=$(echo "$ver" | awk -F. '{printf "%d.%d.%d.%d", $1, $2, $3, $4}')
sed -i -e "s|^application/file_version=.*|application/file_version=\"$winver\"|" \
       -e "s|^application/product_version=.*|application/product_version=\"$winver\"|" game/export_presets.cfg
# the macOS bundle's CFBundleShortVersionString / CFBundleVersion want up to three numbers: 0.1.0-rc1 -> 0.1.0
macver=$(echo "$ver" | grep -oE '^[0-9]+(\.[0-9]+){0,2}' || true); macver=${macver:-0.0.0}
sed -i -e "s|^application/short_version=.*|application/short_version=\"$macver\"|" \
       -e "s|^application/version=.*|application/version=\"$macver\"|" game/export_presets.cfg

out=$here/dist/build
rm -rf "$out"
mkdir -p "$out/win" "$out/linux" "$out/macos" "$out/linux-arm64"
run() { "${nixf[@]}" develop "$here" -c "$@"; }
run godot-mono --headless --path game --import >/dev/null 2>&1 || true
# the terrain's material pack, compressed here because an export's engine has no texture compressor (it would fall back to RGBA8,
# four times the video memory): game/ground/pack.bin, BPTC and ASTC, packed by the include_filter of the presets (World/TerrainMaterials.cs)
(unset RuntimeFrameworkVersion; cd game && run dotnet build >/dev/null) || { echo "the dev build for the ground pack failed: run dotnet build in game/" >&2; exit 1; }
(unset RuntimeFrameworkVersion; run godot-mono --headless --path game -- --bake-ground-pack "$here/game/ground/pack.bin") || { echo "baking the ground pack failed" >&2; exit 1; }
[ -s game/ground/pack.bin ] || { echo "game/ground/pack.bin is missing" >&2; exit 1; }
run godot-mono --headless --path game --export-release "Windows x64"
run godot-mono --headless --path game --export-release "Linux x86_64"
# the macOS export (arm64 half of the universal binary) wants the ETC2/ASTC texture variants imported; the project
# setting is turned on only now, so the Windows and Linux pcks above stay as they were (the mac one is a separate pck)
if grep -q '^\[rendering\]' game/project.godot; then sed -i 's|^\[rendering\]|&\ntextures/vram_compression/import_etc2_astc=true|' game/project.godot
else printf '\n[rendering]\n\ntextures/vram_compression/import_etc2_astc=true\n' >> game/project.godot; fi
run godot-mono --headless --path game --import >/dev/null 2>&1 || true
run godot-mono --headless --path game --export-release "macOS"
# Linux arm64: same setting (ETC2/ASTC imported); the preset ships those variants and not BC7/S3TC
run godot-mono --headless --path game --export-release "Linux arm64"
# the exports built game/.godot/mono/temp with the release runtime version: drop that and build the dev
# assemblies again without it, or the next editor or dev run finds no game classes ("Cannot instantiate C# script")
rm -rf game/.godot/mono/temp/bin game/.godot/mono/temp/obj 2>/dev/null || true
(unset RuntimeFrameworkVersion; cd game && run dotnet build >/dev/null) || { echo "the dev rebuild failed: run dotnet build in game/" >&2; exit 1; }

# the dedicated server (tools/I76.Server) shares the game export's .NET runtime: it is published self-contained (not
# single-file) against the same RuntimeFrameworkVersion, and only i26-server.dll/.deps.json/.runtimeconfig.json go into
# the game's data folder (step 5); the launcher next to i26.exe is the published apphost with its embedded
# relative app path patched to that folder. The artifacts go under the cache, so the dev builds' obj/bin are not touched.
rids=(win-x64 linux-x64 linux-arm64)
[ "${I26_ARMV7:-0}" = 1 ] && rids+=(linux-arm)
for rid in "${rids[@]}"; do
    log=$cache/server-publish-$rid.log
    rm -rf "$cache/server-$rid" "$cache/server-art-$rid"
    run dotnet publish tools/I76.Server/I76.Server.Tool.csproj -c Release -r $rid --self-contained \
        -p:DebugType=none -o "$cache/server-$rid" --artifacts-path "$cache/server-art-$rid" >"$log" 2>&1 || { tail -30 "$log" >&2; echo "server publish ($rid) failed" >&2; exit 1; }
done

# 3. one pck for both, if the two exports agree
cmp -s "$out/win/i26.pck" "$out/linux/i26.pck" || { echo "the two .pck files differ: ship one each" >&2; exit 1; }
for dir in win linux linux-arm64; do [ -d "$out"/$dir/data_I76.Game_* ] || { echo "no .NET data folder in $dir" >&2; exit 1; }; done

# a nix-store RUNPATH in the shipped runtime would mean the nix pack got in
for d in linux linux-arm64; do
    if grep -rlaq '/nix/store' "$out"/$d/data_I76.Game_*/libcoreclr.so; then echo "the $d runtime is nix's, not the official pack" >&2; exit 1; fi
done
# every ELF in a Linux build is of that build's machine: x86-64 (62), AArch64 (183) or ARM (40, 32-bit EABI5 hard-float)
elfcheck() {   # elfcheck MACHINE DIR-OR-FILE...
python3 - "$@" <<'PY'
import os, struct, sys
want, *paths = sys.argv[1:]
M = {"x86_64": 62, "aarch64": 183, "arm": 40}[want]
n = 0
files = []
for p in paths:
    if os.path.isdir(p):
        for d, _, fs in os.walk(p): files += [os.path.join(d, f) for f in fs]
    else: files.append(p)
for f in sorted(files):
    with open(f, "rb") as fh: h = fh.read(64)
    if h[:4] != b"\x7fELF": continue
    cls, data = h[4], h[5]
    if data != 1: sys.exit(f"{f}: not little-endian")
    mach, = struct.unpack("<H", h[18:20])
    if mach != M: sys.exit(f"{f}: ELF machine {mach}, wanted {want} ({M})")
    if want == "arm":
        flags, = struct.unpack("<I", h[36:40])
        if cls != 1: sys.exit(f"{f}: not 32-bit")
        if flags >> 24 != 5 or not flags & 0x400: sys.exit(f"{f}: not EABI5 hard-float (flags {flags:#x})")
    elif cls != 2: sys.exit(f"{f}: not 64-bit")
    n += 1
if n == 0: sys.exit("no ELF files found in " + " ".join(paths))
print(f"{n} ELF files, all {want}" + (" (32-bit EABI5 hard-float)" if want == "arm" else ""))
PY
}
echo -n "linux arm64 build: "; elfcheck aarch64 "$out/linux-arm64/i26.arm64" "$out"/linux-arm64/data_I76.Game_* "$cache/server-linux-arm64"
echo -n "linux x86_64 build: "; elfcheck x86_64 "$out/linux/i26.x86_64" "$out"/linux/data_I76.Game_* "$cache/server-linux-x64"

# 4. nothing that must not ship
# user://log_settings.cfg is only the name of the settings-override file in project.binary, not a packed file
listing=$(strings -a "$out/win/i26.pck" | grep -E '^(res|user)://' | grep -vx 'user://log_settings.cfg' | sort -u)
if grep -q '^user://' <<<"$listing"; then echo "user:// files packed:" >&2; grep '^user://' <<<"$listing" >&2; exit 1; fi
if grep -qiE '\.(pcx|zfs|zix|pak|vqm|cbk|map|geo|wav|smk|m16)(\.|$)|loadgame' <<<"$listing"; then echo "game data packed" >&2; exit 1; fi
if ! grep -q 'res://Assets/boot_splash.png' <<<"$listing"; then echo "our boot splash is missing" >&2; exit 1; fi
# (a pck lists included files without the res:// prefix, so look in the pck's own strings, not the res:// listing)
if ! strings -a "$out/win/i26.pck" | grep -q '^ground/pack.bin'; then echo "the ground material pack (ground/pack.bin) is not in the pck: release builds would hold the terrain materials uncompressed" >&2; exit 1; fi
echo "$listing" > "$out/pck-listing.txt"
# the arm64 pck: the same rules, and ETC2/ASTC textures only
alist=$(strings -a "$out/linux-arm64/i26.pck" | grep -E '^(res|user)://' | grep -vx 'user://log_settings.cfg' | sort -u)
if grep -q '^user://' <<<"$alist" || grep -qiE '\.(pcx|zfs|zix|pak|vqm|cbk|map|geo|wav|smk|m16)(\.|$)|loadgame' <<<"$alist"; then echo "arm64 pck: user:// or game data packed" >&2; exit 1; fi
grep -q 'res://Assets/boot_splash.png' <<<"$alist" || { echo "arm64 pck: boot splash missing" >&2; exit 1; }
strings -a "$out/linux-arm64/i26.pck" | grep -q '^ground/pack.bin' || { echo "arm64 pck: the ground material pack (ASTC) is missing" >&2; exit 1; }
# its texture imports must name ETC2/ASTC variants (path.etc2=) and no BC7/S3TC ones (path.s3tc=)
[ "$(strings -a "$out/linux-arm64/i26.pck" | grep -c 'path\.etc2=')" -gt 0 ] || { echo "arm64 pck: no ETC2/ASTC texture variants" >&2; exit 1; }
[ "$(strings -a "$out/linux-arm64/i26.pck" | grep -c 'path\.s3tc=')" -eq 0 ] || { echo "arm64 pck: S3TC/BPTC variants packed" >&2; exit 1; }
echo "arm64 pck: $(wc -l <<<"$alist") paths, $(stat -c %s "$out/linux-arm64/i26.pck") bytes (x86_64: $(stat -c %s "$out/linux/i26.pck"))"
echo "pck: $(wc -l <<<"$listing") paths, no user://, no game files"

# 5. assemble and zip: one zip per platform, each with the same top-level folder i26-VERSION/
zipdir() {   # zipdir STAGE_PARENT NAME DEST
python3 - "$1" "$2" "$3" <<'PY'
import os, sys, zipfile
out, name, dest = sys.argv[1:]
root = os.path.join(out, name)
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, dirs, files in sorted(os.walk(root)):
        for f in sorted(files + [x for x in dirs if os.path.islink(os.path.join(d, x))]):
            p = os.path.join(d, f)
            if os.path.islink(p):   # kept as a symlink (the macOS bundle may have some): the target is the content
                zi = zipfile.ZipInfo(os.path.relpath(p, out), (1980, 1, 1, 0, 0, 0))
                zi.create_system = 3
                zi.external_attr = (0o120755 << 16)
                z.writestr(zi, os.readlink(p))
                continue
            zi = zipfile.ZipInfo.from_file(p, os.path.relpath(p, out))
            zi.compress_type = zipfile.ZIP_DEFLATED
            with open(p, "rb") as fh: z.writestr(zi, fh.read(), zipfile.ZIP_DEFLATED, 9)
PY
}
# add_server PLATFORM STAGE: the dedicated server on the game's runtime. Every file the server publish and the game's
# data folder both have must be the same (the runtime and System.*; our I76.*.dll are built by two builds of the same
# source, so the game's copies serve both). Anything else the server needs is copied in; a mismatch fails the release.
add_server() {
python3 - "$1" "$2" "$cache" <<'PY'
import glob, os, shutil, sys
plat, stage, cache = sys.argv[1:]
rid, host, sep = ("win-x64", "i26-server.exe", "\\") if plat == "windows" else ("linux-arm64" if plat == "linux-arm64" else "linux-x64", "i26-server", "/")
pub = os.path.join(cache, "server-" + rid)
data = glob.glob(os.path.join(stage, "data_I76.Game_*"))[0]
rel = os.path.basename(data)
for f in sorted(os.listdir(pub)):
    if f == host: continue
    dst = os.path.join(data, f)
    if os.path.exists(dst):
        if f.startswith("I76.") and f.endswith(".dll"): continue
        if open(dst, "rb").read() != open(os.path.join(pub, f), "rb").read():
            sys.exit(f"server/game runtime mismatch: {f} differs between the server publish and the game export")
    else:
        shutil.copy(os.path.join(pub, f), dst)
        if not f.startswith("i26-server."): print("note: server file not in the game export:", f)
# the apphost: replace its embedded app path ("i26-server.dll", zero-padded to 1024 bytes) by the one in the data folder
d = bytearray(open(os.path.join(pub, host), "rb").read())
old = b"i26-server.dll\0"
i = d.find(old)
if i < 0 or d.find(old, i + 1) >= 0 or d[i:i + 1024].strip(b"i26-server.dl\0") != b"":
    sys.exit("apphost: embedded app path not found")
p = (rel + sep + "i26-server.dll").encode()
d[i:i + 1024] = p + b"\0" * (1024 - len(p))
out = os.path.join(stage, host)
open(out, "wb").write(d)
os.chmod(out, 0o755)
size = sum(os.path.getsize(os.path.join(data, f)) for f in os.listdir(data) if f.startswith("i26-server.")) + len(d)
print(f"server ({plat}): {size} bytes of its own files (launcher + i26-server.dll/.json), runtime shared with the game")
PY
}
for plat in windows linux linux-arm64; do
    src=$plat; [ $plat = windows ] && src=win
    exe=i26.x86_64; [ $plat = windows ] && exe=i26.exe; [ $plat = linux-arm64 ] && exe=i26.arm64
    stage=$out/stage-$plat/$name
    mkdir -p "$stage"
    cp "$out/$src/$exe" "$out/$src/i26.pck" "$stage/"
    cp -r "$out"/$src/data_I76.Game_* "$stage/"
    add_server $plat "$stage"
    [ $plat != windows ] && chmod +x "$stage/$exe"
    sed -e "s/@VERSION@/$ver/g" -e "s/@BUILD@/$stamp/g" tools/release/README.txt > "$stage/README.txt"
    cp tools/release/THIRD-PARTY-NOTICES.txt "$stage/"
    [ -f game/vfx/SOURCE.txt ] && { echo; cat game/vfx/SOURCE.txt; } >> "$stage/THIRD-PARTY-NOTICES.txt"
    [ -f game/ground/SOURCE.txt ] && { echo; cat game/ground/SOURCE.txt; } >> "$stage/THIRD-PARTY-NOTICES.txt"
    dest=$here/dist/$name-$plat.zip
    rm -f "$dest"
    zipdir "$out/stage-$plat" "$name" "$dest"
    echo "wrote dist/$name-$plat.zip ($(du -h "$dest" | cut -f1))"
done
# macOS: Godot's I26.zip holds I26.app; unpack it (modes and symlinks kept) into i26-VERSION/ with the README and notices.
# The dedicated server is not in this zip: it would need an apphost per architecture beside a signed bundle.
stage=$out/stage-macos/$name
mkdir -p "$stage"
python3 - "$out/macos/I26.zip" "$stage" <<'PY'
import os, stat, sys, zipfile
src, dst = sys.argv[1:]
with zipfile.ZipFile(src) as z:
    for i in z.infolist():
        p = os.path.join(dst, i.filename)
        mode = i.external_attr >> 16
        if i.is_dir(): os.makedirs(p, exist_ok=True); continue
        os.makedirs(os.path.dirname(p), exist_ok=True)
        if stat.S_ISLNK(mode): os.symlink(z.read(i).decode(), p); continue
        with open(p, "wb") as f: f.write(z.read(i))
        os.chmod(p, mode & 0o777 or 0o644)
PY
# Godot 4.7.2's own signer (the one it uses off a Mac) writes the entitlements' DER blob with long-form lengths where DER
# wants the short form (godotengine/godot#124317, fixed for 4.8), and macOS 26 kills the app at launch for it. rcodesign
# signs the bundle again, ad hoc, keeping each binary's identifier, flags (hardened runtime) and entitlements from
# Godot's signature, and writes the DER blob as Apple's codesign does.
run rcodesign sign "$stage/I26.app" >"$cache/rcodesign.log" 2>&1 || { tail -30 "$cache/rcodesign.log" >&2; echo "rcodesign failed" >&2; exit 1; }
sed -e "s/@VERSION@/$ver/g" -e "s/@BUILD@/$stamp/g" tools/release/README.txt > "$stage/README.txt"
cp tools/release/THIRD-PARTY-NOTICES.txt "$stage/"
[ -f game/vfx/SOURCE.txt ] && { echo; cat game/vfx/SOURCE.txt; } >> "$stage/THIRD-PARTY-NOTICES.txt"
[ -f game/ground/SOURCE.txt ] && { echo; cat game/ground/SOURCE.txt; } >> "$stage/THIRD-PARTY-NOTICES.txt"
dest=$here/dist/$name-macos.zip
rm -f "$dest"
zipdir "$out/stage-macos" "$name" "$dest"
# the macOS zip as shipped: universal (both runtimes, one data folder each), ad-hoc signed (by Godot, then rcodesign); checked without a Mac
python3 - "$dest" "$name/" <<'PY' || { rm -f "$dest"; exit 1; }
import os, plistlib, struct, sys, zipfile
z = zipfile.ZipFile(sys.argv[1]); pre = sys.argv[2]
names = {n[len(pre):] for n in z.namelist() if n.startswith(pre)}
def read(n): return z.read(pre + n)
def die(m): sys.exit("macOS bundle: " + m)
def slices(b):   # [(arch, bytes_of_slice)] of a Mach-O, fat or thin
    CPU = {0x01000007: "x86_64", 0x0100000c: "arm64"}
    if b[:4] == b"\xca\xfe\xba\xbe":
        n = struct.unpack(">I", b[4:8])[0]
        out = []
        for i in range(n):
            ct, _, off, size, _ = struct.unpack(">IIIII", b[8 + 20 * i:28 + 20 * i])
            out.append((CPU.get(ct, hex(ct)), b[off:off + size]))
        return out
    if b[:4] == b"\xcf\xfa\xed\xfe": return [(CPU.get(struct.unpack("<I", b[4:8])[0], "?"), b)]
    die("not a Mach-O")
def signature(sl):  # the blobs of the embedded signature (LC_CODE_SIGNATURE, 0x1d) by magic, None if unsigned
    ncmds = struct.unpack("<I", sl[16:20])[0]; off = 32
    for _ in range(ncmds):
        cmd, size = struct.unpack("<II", sl[off:off + 8])
        if cmd == 0x1d:
            do, ds = struct.unpack("<II", sl[off + 8:off + 16]); sb = sl[do:do + ds]
            n = struct.unpack(">I", sb[8:12])[0]
            offs = [struct.unpack(">I", sb[16 + 8 * i:20 + 8 * i])[0] for i in range(n)]
            return {struct.unpack(">I", sb[o:o + 4])[0]: sb[o + 8:o + struct.unpack(">I", sb[o + 4:o + 8])[0]] for o in offs}
        off += size
    return None
def signed(sl): return signature(sl) is not None
# The entitlements' DER blob (0xfade7172) is checked at launch: with a length not in the shortest form the app is killed
# before it runs ("Launchd job spawn failed"). Wanted: Apple's form, as codesign and rcodesign write it, [APPLICATION 16]
# { INTEGER 1, value }, a dict as [CONTEXT 16] { SEQUENCE { UTF8String key, value } ... }, the shortest lengths, true as ff,
# saying what the XML blob (0xfade7171) says.
def der(b, i):   # one element at i: (tag, contents, next)
    t, n = b[i], b[i + 1]; i += 2
    if n & 0x80:
        k = n & 0x7f
        if not 1 <= k <= 4 or b[i] == 0: die("entitlements DER: a length not in the shortest form")
        n = int.from_bytes(b[i:i + k], "big"); i += k
        if n < 0x80: die("entitlements DER: a length not in the shortest form")
    if i + n > len(b): die("entitlements DER: truncated")
    return t, b[i:i + n], i + n
def items(c):
    i, out = 0, []
    while i < len(c): t, v, i = der(c, i); out.append((t, v))
    return out
def value(t, v):
    if t == 0x01 and v in (b"\xff", b"\x00"): return v == b"\xff"
    if t == 0x0c: return v.decode()
    if t == 0x02: return int.from_bytes(v, "big", signed=True)
    if t == 0x30: return [value(*e) for e in items(v)]
    if t == 0xb0:
        d = {}
        for et, ev in items(v):
            kv = items(ev)
            if et != 0x30 or len(kv) != 2 or kv[0][0] != 0x0c: die("entitlements DER: a dict entry is not { key, value }")
            d[kv[0][1].decode()] = value(*kv[1])
        return d
    die("entitlements DER: tag %#x (%d bytes) is not Apple's form" % (t, len(v)))
def entitlements_ok(sl, arch):
    sig = signature(sl)
    xml, d = sig.get(0xfade7171), sig.get(0xfade7172)
    if xml is None or d is None: die("%s: no entitlements (the .NET runtime needs allow-jit under the hardened runtime)" % arch)
    t, c, end = der(d, 0)
    if t != 0x70 or end != len(d): die("%s: entitlements DER starts %s, not Apple's 70 .. 02 01 01 (Godot's own signer's?)" % (arch, d[:8].hex(" ")))
    e = items(c)
    if len(e) != 2 or e[0] != (0x02, b"\x01"): die("%s: entitlements DER has no version 1" % arch)
    if value(*e[1]) != plistlib.loads(xml): die("%s: the DER entitlements differ from the XML ones" % arch)
plist = plistlib.loads(read("I26.app/Contents/Info.plist"))
exe = plist["CFBundleExecutable"]
if "I26.app/Contents/MacOS/" + exe not in names: die("CFBundleExecutable %s is not in Contents/MacOS" % exe)
if plist["CFBundleIdentifier"] != "io.github.vahokif.i26": die("bundle id " + plist["CFBundleIdentifier"])
if "I26.app/Contents/_CodeSignature/CodeResources" not in names: die("no _CodeSignature")
if not z.getinfo(pre + "I26.app/Contents/MacOS/" + exe).external_attr >> 16 & 0o111: die("the executable is not executable")
main = slices(read("I26.app/Contents/MacOS/" + exe))
if sorted(a for a, _ in main) != ["arm64", "x86_64"]: die("main binary is %s, not universal" % [a for a, _ in main])
if not all(signed(sl) for _, sl in main): die("main binary has no embedded signature")
for arch, sl in main: entitlements_ok(sl, arch)
n = 0
for nm in sorted(names):
    if not nm.endswith(".dylib") and not nm.endswith("createdump"): continue
    want = "arm64" if "macos_arm64" in nm else "x86_64" if "macos_x86_64" in nm else None
    b = read(nm)
    if want is None: die("native library outside the per-architecture data folders: " + nm)
    if [a for a, _ in slices(b)] != [want]: die("%s is %s, wanted %s" % (nm, [a for a, _ in slices(b)], want))
    if b"/nix/store" in b: die(nm + " mentions /nix/store")
    n += 1
print("macOS: %s is arm64 + x86_64 and signed, its entitlements in Apple's DER form; %d native libs, each one architecture, none from nix; Info.plist -> %s" % (exe, n, exe))
PY
echo "wrote dist/$name-macos.zip ($(du -h "$dest" | cut -f1))"
rm -f "$here/dist/$name.zip"

# OPT-IN (I26_ARMV7=1 tools/release.sh VERSION): it builds and its checks pass, but it is UNVERIFIED on hardware: under qemu-user
# (11.1.2, nix's armv7 glibc) the server crashes with SIGBUS at varying places, even in the startup JIT, so a qemu fault cannot be
# told from a real one; the arm64 server runs fine there. Ship it only after a run on a real 32-bit ARM board.
# The dedicated server alone for 32-bit ARM Linux: the linux-arm self-contained publish as it is (launcher, i26-server.dll,
# the runtime), plus the server README and the notices. No Godot: the publish must hold no GodotSharp, and every ELF in it
# must be 32-bit ARM EABI5 hard-float. The server reads the game's files from an install as the others do (GameDiscovery).
if [ "${I26_ARMV7:-0}" = 1 ]; then
sname=i26-server-$ver
stage=$out/stage-server-armv7/$sname
rm -rf "$out/stage-server-armv7"; mkdir -p "$stage"
cp -r "$cache/server-linux-arm/." "$stage/"
if ls "$stage" | grep -qi 'godot'; then echo "the armv7 server publish holds Godot files" >&2; exit 1; fi
[ -x "$stage/i26-server" ] || { echo "no i26-server launcher in the armv7 publish" >&2; exit 1; }
if grep -laq '/nix/store' "$stage"/libcoreclr.so; then echo "the armv7 runtime is nix's, not the official pack" >&2; exit 1; fi
echo -n "armv7 server: "; elfcheck arm "$stage"
sed -e "s/@VERSION@/$ver/g" -e "s/@BUILD@/$stamp/g" tools/release/README-server.txt > "$stage/README.txt"
{ echo "(This server-only package holds no Godot, no Jolt Physics and none of the free art below: those entries apply to the"
  echo "full game builds. The .NET runtime and LiteNetLib are in this folder.)"; echo; cat tools/release/THIRD-PARTY-NOTICES.txt; } > "$stage/THIRD-PARTY-NOTICES.txt"
dest=$here/dist/$sname-linux-armv7.zip
rm -f "$dest"
zipdir "$out/stage-server-armv7" "$sname" "$dest"
echo "wrote dist/$sname-linux-armv7.zip ($(du -h "$dest" | cut -f1))"
fi
