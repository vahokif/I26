#!/usr/bin/env bash
# Runs the network tests and fails unless every listed test ran. A test host that crashes (an exception on a
# background thread) aborts the run but the summary line still says "Passed!" for the tests it got through.
# Usage: tools/test-net.sh [extra dotnet test args]   (default filter: Load!=Sensitive)
set -u
cd "$(dirname "$0")/.."
filter='Load!=Sensitive'
[ "${1:-}" = "--filter" ] && { filter=$2; shift 2; }
want=$(dotnet test tests/I76.Net.Tests --filter "$filter" --list-tests 2>/dev/null | sed -n '/^The following Tests are available/,$p' | tail -n +2 | grep -c .)
out=$(dotnet test tests/I76.Net.Tests --filter "$filter" "$@" 2>&1); code=$?
echo "$out" | tail -n 30
ran=$(echo "$out" | sed -n 's/.*Total: *\([0-9]*\).*/\1/p' | tail -n 1)
if [ "$code" -ne 0 ] || echo "$out" | grep -q 'Test Run Aborted'; then echo "FAILED (exit $code)"; exit 1; fi
if [ "${ran:-0}" -lt "$want" ]; then echo "SHORT RUN: $ran of $want tests ran"; exit 1; fi
echo "OK: $ran of $want tests ran"
