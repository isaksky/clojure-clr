# Clojure.Main Startup Performance

## Goal

Reduce default `Clojure.Main` startup time enough that modern persisted AOT produces a user-visible startup improvement, not only a functional compiled namespace path.

Primary target for the current branch:

- Command: `dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll -e "(println :ok)"`
- Environment: Release `net10.0`, warm filesystem, no `CLOJURE_LOAD_PATH` override
- Required default behavior: compiled runtime `clojure.*.clj.dll` files are used from the `Clojure.Main` output directory
- Pass threshold: median of 10 runs <= 750 ms
- Pass threshold: p95 of 10 runs <= 1000 ms

Secondary target:

- Command exercises a first library require, for example:

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  -e "(require 'clojure.string) (println (clojure.string/upper-case \"ok\"))"
```

The secondary target should be measured and tracked before declaring the broader startup goal complete, but the exact threshold should be set after the first benchmark/profiling pass.

## Tracking

- Parent Bead: `clojure-clr-qiz` - Define and meet Clojure.Main startup performance gate
- Related goal: [Goal.md](../Goal.md)
- Current plan: [plan.md](../plan.md)
- AOT verification: [aot-verification-plan.md](aot-verification-plan.md)

## Current Evidence

Measurements from this branch after commit `d7a16a3d`:

- Before default copy-path fix, trivial `Clojure.Main -e "(println :ok)"` startup was about 5.2-5.4 seconds because default `Clojure.Main` output contained source files but not compiled runtime DLLs.
- With compiled runtime DLLs supplied manually through `CLOJURE_LOAD_PATH`, trivial startup was about 2.9 seconds.
- After fixing project-build copy paths so default `Clojure.Main` output contains compiled runtime DLLs, Debug `net9.0`/`net10.0` trivial startup was about 1.08-1.09 seconds.
- Release `net10.0` warm runs were about 1.00-1.01 seconds.

The functional AOT milestone is therefore not enough by itself. The remaining work is to profile and reduce the roughly one-second compiled-runtime startup path.

## Verification Commands

Build the Release output and compiled runtime assemblies:

```sh
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0
```

Confirm compiled runtime DLLs are present in the default `Clojure.Main` output:

```sh
find Clojure/Clojure.Main/bin/Release/net10.0 \
  -maxdepth 1 \
  -type f \
  -name 'clojure*.clj.dll' \
  | sort
```

Initial ad hoc timing command:

```sh
/usr/bin/time -p \
  dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  -e "(println :ok)"
```

The first implementation task should replace ad hoc timing with a repeatable benchmark command or script that records at least 10 runs and reports median and p95.

## Constraints

- Preserve Clojure load/eval semantics.
- Preserve source-free loading of compiled runtime namespaces.
- Do not regress modern persisted AOT regression coverage.
- Do not mark this goal complete solely because startup improves; it must meet the numeric threshold.
- If a faster result depends on packaging changes such as ReadyToRun or native publishing, record that separately from compiler/runtime changes.

## Attempt Log

| Date | Commit | Hypothesis | Change | Result | Status |
| --- | --- | --- | --- | --- | --- |
| 2026-05-22 | `d7a16a3d` | Default startup was still source-loading because compiled runtime DLLs were not copied into `Clojure.Main` output. | Replaced solution-only `$(SolutionDir)` copy/resource paths with project-relative `$(MSBuildThisFileDirectory)` paths. | Release trivial startup improved to about 1.00-1.01 seconds. | Kept |

## Completion Evidence

Do not mark `clojure-clr-qiz` complete until this file records:

- The benchmark command or script used.
- Runtime, SDK, OS, architecture, configuration, and commit.
- At least 10 measured runs for the primary command.
- Median <= 750 ms and p95 <= 1000 ms for the primary command.
- Secondary require-path measurement.
- Relevant AOT regression tests passing, or a precise explanation of any tests that could not be run.
