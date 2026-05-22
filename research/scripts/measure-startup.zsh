#!/usr/bin/env zsh
set -euo pipefail

zmodload zsh/datetime

runs=${RUNS:-10}
warmups=${WARMUPS:-2}

if [[ ${1:-} == "--" ]]; then
  shift
fi

if (( $# == 0 )); then
  command=(
    dotnet
    Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll
    -e
    "(println :ok)"
  )
else
  command=("$@")
fi

unset CLOJURE_LOAD_PATH
unset CLOJURE_STARTUP_TRACE

out_file=${TMPDIR:-/tmp}/clojure-clr-startup.$$.out
trap 'rm -f "$out_file"' EXIT
times=()

function timed_run() {
  local start=$EPOCHREALTIME
  "${command[@]}" > "$out_file"
  local end=$EPOCHREALTIME
  awk -v start="$start" -v end="$end" 'BEGIN { printf "%.1f", (end - start) * 1000.0 }'
}

for ((i = 1; i <= warmups; i++)); do
  timed_run > /dev/null
done

print "run,ms"
for ((i = 1; i <= runs; i++)); do
  ms=$(timed_run)
  times+=("$ms")
  print "$i,$ms"
done

sorted=("${(@n)times}")
median=$(printf "%s\n" "${sorted[@]}" | awk -v n="$runs" '
  { a[NR] = $1 }
  END {
    if (n % 2 == 1) {
      printf "%.1f", a[(n + 1) / 2]
    } else {
      printf "%.1f", (a[n / 2] + a[n / 2 + 1]) / 2.0
    }
  }')
p95_index=$(awk -v n="$runs" 'BEGIN { print int(0.95 * n + 0.999999) }')
p95=${sorted[$p95_index]}

print "median_ms=$median"
print "p95_ms=$p95"
