#!/usr/bin/env bash
# Render one frame of the game off-screen and save it from inside the engine.
#   tools/shot.sh OUT.png [game args...]     e.g. --mission a01 --1997 --cam x,y,z,yaw,pitch
#   I76_SHOT_SIZE=640x480 renders at another size (the original's 4:3, to lay over a frame of the original)
#   I76_SHOT_FPS=60 steps the game at a fixed rate, as if it ran that fast (short effects then last their frames)
#   I76_SHOT_MAX_FPS=30 also holds it to that many frames a real second (paced by the game: Godot's --max-fps does nothing under --fixed-fps): with I76_SHOT_FPS the same, the capture runs in
#     real time, for what runs on the wall clock (a listen server and bots joining it)
#   I76_SHOT_FRAMES=N quits after N frames (default 12); the shot is frame N-2
#   I76_SHOT_TIMEOUT=S stops the game after S seconds (default 600), for a long capture
#   I76_SHOT_SERIES=K also saves the last K frames, I76_SHOT_EVERY=N every Nth sim tick (read by the game)
#   movies (intro, briefings) are skipped unless I76_SHOT_MOVIES=1
#   the game seeds its rand stream with 1 under --screenshot (not the clock), so a capture repeats; --seed N for another
#   I76_SHOT_GL=1 renders with Godot's OpenGL (Compatibility) renderer instead of Vulkan Forward+ (what a machine without Vulkan/D3D12 gets);
#     under GPU mode the game runs on EGL in the headless Weston, under software on Mesa's llvmpipe
#   I76_SHOT_SETTINGS=1 leaves the renderer off the command line, so Graphic Detail's Renderer (the override file in user://, see I76_SHOT_DATA) picks it
#   I76_FRAME_PROFILE=1 adds Godot's --gpu-profile, whose timestamps the game's FrameProfiler groups into passes
# Two ways to render, both off-screen and private (nothing reaches the desktop, its displays or settings):
#   GPU, the default: a private headless Weston (its own XDG_RUNTIME_DIR under /tmp, its own socket, an
#     output of I76_SHOT_SIZE under the kiosk shell, so the window is exactly that size), the game on Wayland
#     with the radeon Vulkan driver and no DISPLAY. Several times faster than software (m01, 60 frames at
#     960x540, whole run: ~5 s against ~35 s on a quiet machine, ~10 s against ~50 s on a busy one). If Weston
#     or the driver isn't there, or the game never brings Vulkan up on it, it says so and falls back to software.
#   software (I76_SHOT_GPU=0): a private Xvfb display with Mesa's software Vulkan (lavapipe); slow, but runs
#     anywhere. I76_SHOT_DISPLAY picks the X display, else the first free one from :77.
#   I76_SHOT_ICD=/path/to/gpu_icd.json picks another GPU's Vulkan driver. Without it the radeon driver is looked up in the NixOS
#     driver dir (/run/opengl-driver/..., only where it exists); elsewhere the GPU path falls back to software unless you set it.
#   GODOT=/path/to/godot names the Godot .NET binary (default: godot-mono, else godot on PATH).
#   The two agree to within run-to-run noise but for one seen difference: the modern look's car side windows
#   are solid black on lavapipe and dark glass on the GPU (which is right isn't settled).
# Each run gets its own display / compositor and its own user:// (see below), so several can run at once.
# Godot 4.7 on Wayland can hang in its own exit (about one run in twenty-five here); the game's ExitWatchdog
#   wakes it ("exit watchdog: ..." on stderr), so a run ends; I76_EXIT_WATCHDOG=0 leaves that out.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "$1"); shift
game_args=("$@")
size=${I76_SHOT_SIZE:-1280x720}
gpu=${I76_SHOT_GPU:-1}
cache=${XDG_CACHE_HOME:-$HOME/.cache}/i76-shot
mkdir -p "$cache"
icd_dir=${I76_SHOT_ICD_DIR:-/run/opengl-driver/share/vulkan/icd.d}   # NixOS only; absent elsewhere
godot_bin=${GODOT:-}
[ -n "$godot_bin" ] || { command -v godot-mono >/dev/null && godot_bin=godot-mono || godot_bin=godot; }

# I76_INSTALL=none leaves --install out (the no-install screen, with a fresh I76_SHOT_DATA so nothing is found in the user folder)
install_args=(--install "${I76_INSTALL:?set I76_INSTALL (none: no install)}")
[ "$I76_INSTALL" = none ] && { install_args=(); unset I76_INSTALL; }   # and the game must not see "none" either

if [ -n "${I76_SHOT_SETTINGS:-}" ]; then render_args=(); gpu_ok='OpenGL API|^Vulkan .* Using Device'   # no renderer on the command line: the Graphic Detail Renderer row's override file (in I76_SHOT_DATA) decides
elif [ -n "${I76_SHOT_GL:-}" ]; then render_args=(--rendering-driver opengl3 --rendering-method gl_compatibility); gpu_ok='OpenGL API'
else render_args=(--rendering-driver vulkan); gpu_ok='^Vulkan .* Using Device'; fi

godot() { # godot [display args...]: the game, with the shot args
    timeout ${I76_SHOT_TIMEOUT:-600} "$godot_bin" --audio-driver Dummy ${I76_SHOT_FPS:+--fixed-fps $I76_SHOT_FPS} ${I76_SHOT_MAX_FPS:+--max-fps $I76_SHOT_MAX_FPS} ${I76_FRAME_PROFILE:+--gpu-profile} "$@" --path "$here/game" --windowed --resolution "$size" -- \
        "${install_args[@]}" $([ -n "${I76_SHOT_MOVIES:-}" ] || echo --skip-movies) --screenshot "$out" --quit-after "${I76_SHOT_FRAMES:-12}" "${game_args[@]}"
}

# user:// goes to a scratch tree, never the player's own (settings, input.map, trip progress, saves):
# I76_SHOT_DATA names it (a prepared one, for a test that needs a state), else one per slot (X display,
# or GPU slot), kept between runs for the shader cache. I76_SHOT_LIVE_DATA=1 uses the real one.
use_data() {
    [ -z "${I76_SHOT_LIVE_DATA:-}" ] || return 0
    # not under TMPDIR: nix develop's is removed with its shell, and the shader cache with it
    export XDG_DATA_HOME=${I76_SHOT_DATA:-$cache/data-$1}
    mkdir -p "$XDG_DATA_HOME"
}

cleanup=()
trap 'set +e; for c in "${cleanup[@]}"; do eval "$c"; done' EXIT

no_gpu() { echo "shot.sh: $*" >&2; gpu_failed=1; return 1; }
shot_gpu() { # sets gpu_failed if the GPU path couldn't be brought up (then nothing was rendered)
    local tool
    for tool in weston flock; do
        command -v $tool >/dev/null || no_gpu "no $tool on PATH (install weston and util-linux, or use I76_SHOT_GPU=0)" || return
    done
    # the Vulkan driver of the GPU: I76_SHOT_ICD names its ICD json (e.g. nvidia_icd.json, intel_icd.x86_64.json);
    # the default looks for radeon (RADV) in the NixOS driver dir
    local icd=${I76_SHOT_ICD:-}
    [ -n "$icd" ] || [ ! -d "$icd_dir" ] || icd=$(ls $icd_dir/radeon_icd.*.json 2>/dev/null | head -1 || true)
    [ -n "$icd" ] || no_gpu "no Vulkan driver found (no radeon ICD in $icd_dir; set I76_SHOT_ICD to your GPU's ICD json)" || return
    # a slot, held by a lock for the run: names the scratch user:// (two runs never share one)
    local k=0 lock
    while :; do
        exec {lock}>"$cache/gpu-$k.lock"
        flock -n "$lock" && break
        exec {lock}>&-; k=$((k + 1))
    done
    use_data gpu-$k
    # the compositor's own runtime dir: short (the socket path must fit in 108 bytes) and private
    local rt; rt=$(mktemp -d /tmp/i76w.XXXXXX)
    cleanup+=("rm -rf '$rt'")
    # --refresh-rate in mHz: a fast virtual output, so vsync doesn't hold the game to 60 frames a second
    XDG_RUNTIME_DIR=$rt weston --backend=headless --renderer=gl --shell=kiosk --no-config --idle-time=0 \
        --socket=shot --width="${size%x*}" --height="${size#*x}" --refresh-rate=1000000 --log="$rt/weston.log" >"$rt/weston.out" 2>&1 &
    local wpid=$!
    cleanup=("kill $wpid 2>/dev/null; wait $wpid 2>/dev/null" "${cleanup[@]}")
    local i
    for i in $(seq 100); do [ -S "$rt/shot" ] && break; kill -0 $wpid 2>/dev/null || break; sleep 0.05; done
    [ -S "$rt/shot" ] || { tail -n 5 "$rt/weston.out" "$rt/weston.log" >&2 2>/dev/null; no_gpu "headless weston didn't start (above)" || return; }
    rm -f "$out"
    # no DISPLAY: the game must not find the desktop's X server; Wayland is this compositor only
    local log=$rt/godot.log rc=0
    # (and no libdecor plugins: the kiosk shell draws no frames, and the GTK one only complains)
    (unset DISPLAY; export XDG_RUNTIME_DIR=$rt WAYLAND_DISPLAY=shot VK_ICD_FILENAMES=$icd LIBDECOR_PLUGIN_DIR=$rt
     godot --display-driver wayland "${render_args[@]}") 2>&1 | tee "$log" || rc=$?
    if ! grep -qE "$gpu_ok" "$log"; then
        no_gpu "the game didn't bring up Vulkan on the GPU (exit $rc)" || return
    fi
    return $rc
}

shot_soft() {
    # the X display: I76_SHOT_DISPLAY, or the first free one from :77, so captures can run side by side
    local n
    if [ -n "${I76_SHOT_DISPLAY:-}" ]; then n=$I76_SHOT_DISPLAY; else
        n=77; while [ -e /tmp/.X$n-lock ] || [ -e /tmp/.X11-unix/X$n ]; do n=$((n + 1)); done
    fi
    use_data $n
    Xvfb ":$n" -screen 0 ${size}x24 -nolisten tcp >/dev/null 2>&1 &
    local xpid=$!
    cleanup=("kill $xpid 2>/dev/null" "${cleanup[@]}")
    sleep 1
    local icd=; [ ! -d "$icd_dir" ] || icd=$(ls $icd_dir/lvp_icd.*.json 2>/dev/null | head -1 || true)
    [ -n "$icd" ] && export VK_ICD_FILENAMES=$icd
    DISPLAY=:$n godot ${I76_SHOT_GL:+"${render_args[@]}"}
}

if [ "$gpu" != 0 ]; then
    gpu_failed=; rc=0; shot_gpu || rc=$?
    [ -n "$gpu_failed" ] || exit $rc
    echo "shot.sh: falling back to software rendering (Xvfb + lavapipe); I76_SHOT_GPU=0 skips the GPU attempt" >&2
fi
shot_soft
