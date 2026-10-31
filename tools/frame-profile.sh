#!/usr/bin/env bash
# A/B frame profiles: each configuration in each view, several times, the configurations alternating,
# then the median over the runs of each run's median (FrameProfiler's frame-profile-result line).
#
#   tools/frame-profile.sh OUTDIR REPS 'VIEW=game args' ... -- 'CONFIG=ENV=1 ENV2=x' ...
#
#   e.g. tools/frame-profile.sh /tmp/fp 3 'overview=--mission t01 --cam 4050,60,38480,-20,-20' -- 'base=' 'nossao=I76_SSAO=0'
#   A configuration's FP_ROOT=DIR runs another checkout's game (its tools/shot.sh), for before/after in one run.
#
# Runs tools/shot.sh with I76_FRAME_PROFILE=1 (GPU timestamps per pass, CPU times) at I76_SHOT_SIZE
# (default 1920x1080), I76_SHOT_FPS (30) and I76_SHOT_FRAMES (150; the profiler skips the first 30).
# A view's args may carry env settings first, as KEY=VALUE words (e.g. I76_GAME_KEYS=kp8@20-300 --drive ...).
# Other agents share the GPU: the load average before each run goes into the results, and the
# medians over runs are what to read. OUTDIR gets each run's log, results.tsv and summary.txt.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
out=$1 reps=$2; shift 2
views=(); configs=()
while [ $# -gt 0 ] && [ "$1" != -- ]; do views+=("$1"); shift; done
[ $# -gt 0 ] && shift
configs=("$@")
[ ${#configs[@]} -gt 0 ] || configs=("base=")
mkdir -p "$out"
: "${I76_SHOT_SIZE:=1920x1080}" "${I76_SHOT_FPS:=30}" "${I76_SHOT_FRAMES:=150}"
export I76_SHOT_SIZE I76_SHOT_FPS I76_SHOT_FRAMES I76_FRAME_PROFILE=1
for ((r = 1; r <= reps; r++)); do
    for v in "${views[@]}"; do
        vname=${v%%=*}; vargs=${v#*=}
        for c in "${configs[@]}"; do
            cname=${c%%=*}; cenv=${c#*=}
            log=$out/$vname.$cname.$r.log
            load=$(cut -d' ' -f1 /proc/loadavg)
            others=$(pgrep -fc 'godot.linuxbsd' || true)
            # env words of the view and the configuration, then the game args
            words=(); args=()
            for w in $vargs; do if [ ${#args[@]} = 0 ] && [[ $w =~ ^[A-Z0-9_]+= ]]; then words+=("$w"); else args+=("$w"); fi; done
            # FP_ROOT=dir in a configuration runs that checkout's game (before/after comparisons in one alternating run)
            root=$here
            for w in $cenv; do if [[ $w == FP_ROOT=* ]]; then root=${w#FP_ROOT=}; else words+=("$w"); fi; done
            png=$out/$vname.$cname.$r.png
            env ${words[@]+"${words[@]}"} "$root/tools/shot.sh" "$png" ${args[@]+"${args[@]}"} >"$log" 2>&1 &
            spid=$!
            # the game sometimes takes minutes to exit after its last frame: once the result is in, a few
            # seconds' grace, then the game itself is stopped (not shot.sh, which then tears its compositor down)
            while kill -0 $spid 2>/dev/null; do
                if grep -aq '^frame-profile-result:' "$log"; then
                    for _ in 1 2 3 4 5 6; do kill -0 $spid 2>/dev/null || break; sleep 0.5; done
                    gp=$(pgrep -f "godot\.linuxbsd.*--screenshot $png" || true)
                    [ -z "$gp" ] || kill $gp 2>/dev/null || true
                    break
                fi
                sleep 0.5
            done
            wait $spid || true
            res=$(grep -a '^frame-profile-result:' "$log" | tail -1 | cut -d' ' -f2- || true)
            printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$vname" "$cname" "$r" "$load" "$others" "${res:-FAILED}" | tee -a "$out/results.tsv"
        done
    done
done
python3 - "$out/results.tsv" <<'PY' | tee "$out/summary.txt"
import sys, statistics as st, collections
rows = collections.defaultdict(list)
order = []
for line in open(sys.argv[1]):
    v, c, r, load, others, res = line.rstrip('\n').split('\t')
    if res == 'FAILED': continue
    d = dict(kv.split('=') for kv in res.split())
    if (v, c) not in rows: order.append((v, c))
    rows[(v, c)].append(d)
keys = ['gpu.total', 'gpu.shadows', 'gpu.depth', 'gpu.ssao', 'gpu.opaque', 'gpu.sky', 'gpu.msaa', 'gpu.transparent', 'gpu.post',
        'cpu.wall', 'cpu.wall.p95', 'cpu.nodes', 'cpu.nodes.p95', 'cpu.sim', 'cpu.terrain', 'cpu.render', 'prims_k', 'draws']
print('view\tconfig\truns\t' + '\t'.join(keys))
for v, c in order:
    runs = rows[(v, c)]
    med = [st.median(float(d.get(k, 'nan')) for d in runs) for k in keys]
    print(f'{v}\t{c}\t{len(runs)}\t' + '\t'.join(f'{m:.2f}' for m in med))
PY
