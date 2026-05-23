#!/usr/bin/env zsh
set -euo pipefail

script_dir=${0:A:h}
repo_root=${script_dir:h:h}

target_dir=${1:-"$repo_root/Clojure/Clojure.Main/bin/Release/net10.0"}
tfm=${TFM:-${target_dir:t}}
tfm_major=${tfm#net}
tfm_major=${tfm_major%%.*}

if [[ ! -d "$target_dir" ]]; then
  print -u2 "ReadyToRun target directory does not exist: $target_dir"
  exit 1
fi

rid=${RID:-$(dotnet msbuild "$repo_root/Clojure/Clojure.Compile/Clojure.Compile.csproj" \
  -getProperty:NETCoreSdkRuntimeIdentifier \
  -p:TargetFramework="$tfm" \
  -p:TargetFrameworks="$tfm")}

runtime_line=$(dotnet --list-runtimes | awk -v major="$tfm_major" '
  $1 == "Microsoft.NETCore.App" && $2 ~ ("^" major "\\.") { line = $0 }
  END { print line }
')

if [[ -z "$runtime_line" ]]; then
  print -u2 "Could not find a Microsoft.NETCore.App runtime matching $tfm."
  exit 1
fi

runtime_version=$(print -r -- "$runtime_line" | awk '{ print $2 }')
runtime_base=$(print -r -- "$runtime_line" | awk -F '[][]' '{ print $2 }')
runtime_dir="$runtime_base/$runtime_version"

if [[ ! -d "$runtime_dir" ]]; then
  print -u2 "Runtime reference directory does not exist: $runtime_dir"
  exit 1
fi

crossgen2=${CROSSGEN2:-"$HOME/.nuget/packages/microsoft.netcore.app.crossgen2.${rid:l}/$runtime_version/tools/crossgen2"}

if [[ ! -x "$crossgen2" ]]; then
  print -u2 "crossgen2 was not found at: $crossgen2"
  print -u2 "Run a ReadyToRun publish for RID $rid, or set CROSSGEN2 to the tool path."
  exit 1
fi

clj_dlls=("$target_dir"/clojure.*.clj.dll(N))
if (( ${#clj_dlls[@]} == 0 )); then
  print -u2 "No generated Clojure namespace DLLs found in: $target_dir"
  exit 1
fi

tmp_dir=$(mktemp -d "${TMPDIR:-/tmp}/clojure-clr-r2r.XXXXXX")
trap 'rm -rf "$tmp_dir"' EXIT

for input in "${clj_dlls[@]}"; do
  name=${input:t}
  refs=()

  for ref in "$target_dir"/*.dll(N) "$runtime_dir"/*.dll(N); do
    [[ ${ref:t} == "$name" ]] && continue
    refs+=(-r "$ref")
  done

  "$crossgen2" -o "$tmp_dir/$name" "${refs[@]}" "$input" >/dev/null
done

for output in "$tmp_dir"/clojure.*.clj.dll(N); do
  mv "$output" "$target_dir/${output:t}"
done
