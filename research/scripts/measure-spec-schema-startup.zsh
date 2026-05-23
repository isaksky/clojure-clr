#!/usr/bin/env zsh
set -euo pipefail

script_dir=${0:A:h}
repo_root=${script_dir:h:h}

cd "$repo_root"

if [[ "${SKIP_CLJ_R2R:-false}" != "true" ]]; then
  research/scripts/readytorun-generated-clj-dlls.zsh \
    Clojure/Clojure.Main/bin/Release/net10.0
fi

command=(
  dotnet
  Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll
  Clojure/Clojure.Samples/clojure/samples/spec_schema.clj
)

exec research/scripts/measure-startup.zsh -- "${command[@]}"
