#!/usr/bin/env zsh
set -euo pipefail

script_dir=${0:A:h}
repo_root=${script_dir:h:h}

cd "$repo_root"

command=(
  dotnet
  Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll
  Clojure/Clojure.Samples/clojure/samples/stm/teststm.clj
)

if [[ ${SKIP_MACRO_SPECS:-false} == "true" ]]; then
  command=(env CLOJURE_SPEC_SKIP_MACROS=true "${command[@]}")
fi

exec research/scripts/measure-startup.zsh -- "${command[@]}"
