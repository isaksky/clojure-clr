#!/usr/bin/env zsh
set -euo pipefail

zmodload zsh/datetime

script_dir=${0:A:h}
repo_root=${script_dir:h:h}

cd "$repo_root"

configuration=${CONFIGURATION:-Release}
tfm=${TFM:-net10.0}
main_dll=${MAIN_DLL:-"Clojure/Clojure.Main/bin/$configuration/$tfm/Clojure.Main.dll"}
max_ms=${MAX_MS:-500}
runs=${RUNS:-3}
warmups=${WARMUPS:-1}

if [[ ! -f "$main_dll" ]]; then
  print -u2 "Clojure.Main build output was not found: $main_dll"
  print -u2 "Build it first, for example:"
  print -u2 "  dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj -c $configuration -f $tfm -p:TargetFrameworks=$tfm"
  print -u2 "  dotnet build Clojure/Clojure.Main/Clojure.Main.csproj -c $configuration -f $tfm -p:TargetFrameworks=$tfm --no-restore"
  exit 1
fi

if ! [[ "$runs" == <-> ]] || (( runs < 1 )); then
  print -u2 "RUNS must be a positive integer; got: $runs"
  exit 1
fi

if ! [[ "$warmups" == <-> ]]; then
  print -u2 "WARMUPS must be a non-negative integer; got: $warmups"
  exit 1
fi

if ! awk -v max="$max_ms" 'BEGIN { exit !(max > 0) }'; then
  print -u2 "MAX_MS must be a positive number; got: $max_ms"
  exit 1
fi

spec_schema_script="Clojure/Clojure.Samples/clojure/samples/spec_schema.clj"
if [[ ! -f "$spec_schema_script" ]]; then
  print -u2 "spec_schema.clj was not found: $spec_schema_script"
  exit 1
fi

newtonsoft_demo_script="Clojure/Clojure.Samples/clojure/samples/newtonsoft_demo.cljr"
if [[ ! -f "$newtonsoft_demo_script" ]]; then
  print -u2 "newtonsoft_demo.cljr was not found: $newtonsoft_demo_script"
  exit 1
fi

sqlite_demo_script="Clojure/Clojure.Samples/clojure/samples/sqlite_demo.cljr"
if [[ ! -f "$sqlite_demo_script" ]]; then
  print -u2 "sqlite_demo.cljr was not found: $sqlite_demo_script"
  exit 1
fi

target_dir=${main_dll:h}
if [[ "${SKIP_CLJ_R2R:-false}" != "true" ]]; then
  print -u2 "Preparing generated Clojure namespace DLLs as ReadyToRun images in $target_dir"
  research/scripts/readytorun-generated-clj-dlls.zsh "$target_dir"
fi

unset CLOJURE_LOAD_PATH
unset CLOJURE_STARTUP_TRACE
unset CLOJURE_SPEC_SKIP_MACROS

tmp_dir=$(mktemp -d "${TMPDIR:-/tmp}/clojure-clr-main-suite.XXXXXX")
trap 'rm -rf "$tmp_dir"' EXIT

stdout_file="$tmp_dir/stdout"
stderr_file="$tmp_dir/stderr"
feature_script="$tmp_dir/feature_script.clj"

cat > "$feature_script" <<'CLJ'
(ns startup.feature-script
  (:require [clojure.string :as str]))

(defmacro prefix [s]
  `(str "feature:" ~s))

(defprotocol Label
  (label [x]))

(deftype Thing [value]
  Label
  (label [_] (str/upper-case value)))

(defmulti choose :kind)
(defmethod choose :thing [_] (label (Thing. "ok")))

(println (prefix (choose {:kind :thing})))
CLJ

case_ids=(
  expr-baseline
  expr-macro
  expr-destructuring
  expr-protocol-deftype
  expr-multimethod
  expr-lazy-seq
  expr-string-require
  expr-spec-valid
  expr-spec-instrument
  file-feature-script
  file-spec-schema
  file-newtonsoft-demo
  file-sqlite-demo
)

function build_command() {
  local id=$1
  command=(dotnet "$main_dll")

  case "$id" in
    expr-baseline)
      command+=(-e "(println :ok)")
      ;;
    expr-macro)
      command+=(-e "(do (defmacro twice [x] (list '+ x x)) (println (twice 21)))")
      ;;
    expr-destructuring)
      command+=(-e "(let [{:keys [a b] :or {b 2}} {:a 40} [x y] [1 1]] (println (+ a b x y)))")
      ;;
    expr-protocol-deftype)
      command+=(-e "(do (defprotocol P (value [x])) (deftype T [n] P (value [_] n)) (println (value (T. 42))))")
      ;;
    expr-multimethod)
      command+=(-e "(do (defmulti classify :kind) (defmethod classify :a [_] :alpha) (println (classify {:kind :a})))")
      ;;
    expr-lazy-seq)
      command+=(-e "(println (first (map inc (range 10))))")
      ;;
    expr-string-require)
      command+=(-e "(do (require 'clojure.string) (println (clojure.string/upper-case \"ok\")))")
      ;;
    expr-spec-valid)
      command+=(-e "(do (require '[clojure.spec.alpha :as s]) (s/def ::id int?) (println (s/valid? ::id 42)))")
      ;;
    expr-spec-instrument)
      command+=(-e "(do (require '[clojure.spec.alpha :as s] '[clojure.spec.test.alpha :as st]) (defn plus1 [x] (inc x)) (s/fdef plus1 :args (s/cat :x int?) :ret int?) (st/instrument 'user/plus1) (println (plus1 1)))")
      ;;
    file-feature-script)
      command+=("$feature_script")
      ;;
    file-spec-schema)
      command+=("$spec_schema_script")
      ;;
    file-newtonsoft-demo)
      command+=("$newtonsoft_demo_script")
      ;;
    file-sqlite-demo)
      command+=("$sqlite_demo_script")
      ;;
    *)
      print -u2 "Unknown startup case: $id"
      exit 1
      ;;
  esac
}

function elapsed_ms() {
  awk -v start="$1" -v end="$2" 'BEGIN { printf "%.1f", (end - start) * 1000.0 }'
}

function greater_ms() {
  awk -v left="$1" -v right="$2" 'BEGIN { exit !(left > right) }'
}

function within_budget() {
  awk -v actual="$1" -v max="$2" 'BEGIN { exit !(actual <= max) }'
}

function run_once() {
  local id=$1
  local phase=$2
  local index=$3
  local start end rc ms

  build_command "$id"

  start=$EPOCHREALTIME
  set +e
  "${command[@]}" > "$stdout_file" 2> "$stderr_file"
  rc=$?
  set -e
  end=$EPOCHREALTIME
  ms=$(elapsed_ms "$start" "$end")

  if (( rc != 0 )); then
    print -u2 "Case $id failed during $phase $index (exit $rc, ${ms} ms)."
    if [[ -s "$stderr_file" ]]; then
      print -u2 "stderr:"
      sed -n '1,80p' "$stderr_file" >&2
    fi
    if [[ -s "$stdout_file" ]]; then
      print -u2 "stdout:"
      sed -n '1,80p' "$stdout_file" >&2
    fi
    exit 1
  fi

  print -r -- "$ms"
}

print -u2 "Clojure.Main startup suite: RUNS=$runs WARMUPS=$warmups MAX_MS=$max_ms MAIN_DLL=$main_dll"
print "case,run,ms,status"

failures=()
summaries=()

for id in "${case_ids[@]}"; do
  for ((i = 1; i <= warmups; i++)); do
    run_once "$id" warmup "$i" > /dev/null
  done

  case_max=0

  for ((i = 1; i <= runs; i++)); do
    ms=$(run_once "$id" run "$i")
    result_status=pass

    if greater_ms "$ms" "$case_max"; then
      case_max=$ms
    fi

    if ! within_budget "$ms" "$max_ms"; then
      result_status=fail
      failures+=("$id run $i took ${ms} ms")
    fi

    print "$id,$i,$ms,$result_status"
  done

  summaries+=("$id,$case_max")
done

print ""
print "summary_case,max_ms"
for summary in "${summaries[@]}"; do
  print "$summary"
done

if (( ${#failures[@]} > 0 )); then
  print -u2 ""
  print -u2 "Startup gate failed; max allowed is ${max_ms} ms."
  for failure in "${failures[@]}"; do
    print -u2 "  $failure"
  done
  exit 1
fi

print -u2 "Startup gate passed: every measured run was <= ${max_ms} ms."
