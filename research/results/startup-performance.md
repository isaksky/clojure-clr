# Clojure.Main Startup Performance

## Goal

Reduce default `Clojure.Main` startup time enough that modern persisted AOT produces a user-visible startup improvement, not only a functional compiled namespace path.

Primary target for the current branch:

- Command: installed global tool shim `clj-mayne -e "(println :ok)"`
- Baseline command: installed global tool shim `Clojure.Main -e "(println :ok)"`
- Environment: warm filesystem, no `CLOJURE_LOAD_PATH` override
- Required branch behavior: compiled runtime `clojure.*.clj/c.dll` files are
  installed in the tool payload, prepared as ReadyToRun images, and preferred
  over source
- Pass threshold: median of 10 runs <= 500 ms
- Pass threshold: p95 of 10 runs <= 600 ms

Secondary target:

- Command exercises a first library require, for example:

```sh
clj-mayne -e "(require 'clojure.string) (println (clojure.string/upper-case \"ok\"))"
```

The secondary target should be measured and tracked before declaring the broader startup goal complete, but the exact threshold should be set after the first benchmark/profiling pass.

## Tracking

- Parent Bead: `clojure-clr-qiz` - Define and meet Clojure.Main startup performance gate
- Related goal: [Goal.md](../Goal.md)
- Current plan: [plan.md](../plan.md)
- AOT verification: [aot-verification-plan.md](aot-verification-plan.md)

## Current Evidence

Installed global-tool comparison from this branch at `fd5af306` on 2026-05-26
MDT:

- SDK: .NET SDK 10.0.107, host/runtime 10.0.7.
- OS/architecture: macOS 15.7.3 / Darwin arm64.
- Branch command: installed global tool shim `clj-mayne`.
- Baseline command: installed global tool shim `Clojure.Main` from package
  `clojure.main`.
- Package versions: both installed packages reported `1.12.3-alpha8` in
  `dotnet tool list --global`.
- Installed payloads: `clj-mayne` had ReadyToRun namespace DLLs, 48 compiled
  namespaces, and compiled spec namespaces. `Clojure.Main` had IL namespace
  DLLs, 45 compiled namespaces, and source-loaded spec.
- Expression comparison command:

```sh
USERPROFILE=$HOME pwsh -NoProfile -Command \
  './scripts/bench-startup.ps1 -Tools @("clj-mayne","Clojure.Main") -Runs 3 -Warmup 1'
```

- Script comparison command shape:

```sh
RUNS=3 WARMUPS=1 research/scripts/measure-startup.zsh -- \
  clj-mayne Clojure/Clojure.Samples/clojure/samples/spec_schema.clj
```

Selected measured results:

| Startup probe | `clj-mayne` median / p95 | `Clojure.Main` median / p95 | Result |
| --- | ---: | ---: | --- |
| `-e "(println :ok)"` | `174.5 ms` / `193.7 ms` | `676.0 ms` / `681.6 ms` | `3.9x` faster |
| First `clojure.string` require | `185.7 ms` / `199.5 ms` | `697.5 ms` / `713.7 ms` | `3.8x` faster |
| `clojure.spec.alpha` validation | `223.7 ms` / `244.8 ms` | `697.0 ms` / `701.8 ms` | `3.1x` faster |
| `samples/stm/teststm.clj` | `219.4 ms` / `233.1 ms` | `695.8 ms` / `752.6 ms` | `3.2x` faster |
| `samples/spec_schema.clj` | `247.8 ms` / `267.8 ms` | `904.6 ms` / `937.5 ms` | `3.7x` faster |
| `samples/newtonsoft_demo.cljr` | `245.4 ms` / `245.7 ms` | Fails: missing `Newtonsoft.Json.Linq.JObject` | branch completes |
| `samples/sqlite_demo.cljr` | `266.1 ms` / `274.8 ms` | Fails: missing `Microsoft.Data.Sqlite.SqliteConnection` | branch completes |

The observed branch global-tool range was `173.6-274.8 ms`; the installed
`Clojure.Main` baseline took `651.6-937.5 ms` on comparable successful probes
and failed the two external-package script probes.

Direct `dotnet Clojure.Main.dll` measurements from the build output were also
taken during this update, but the installed global-tool comparison above is the
user-facing benchmark for this branch.

Measurements from this branch after commit `d7a16a3d`:

- Before default copy-path fix, trivial `Clojure.Main -e "(println :ok)"` startup was about 5.2-5.4 seconds because default `Clojure.Main` output contained source files but not compiled runtime DLLs.
- With compiled runtime DLLs supplied manually through `CLOJURE_LOAD_PATH`, trivial startup was about 2.9 seconds.
- After fixing project-build copy paths so default `Clojure.Main` output contains compiled runtime DLLs, Debug `net9.0`/`net10.0` trivial startup was about 1.08-1.09 seconds.
- Release `net10.0` warm runs were about 1.00-1.01 seconds.

Measurements from the working tree based on commit `3d17c436` on 2026-05-22:

- SDK: .NET SDK 10.0.105, host/runtime 10.0.5.
- OS/architecture: macOS 15.7 / Darwin 24.6.0, arm64.
- Configuration: Release `net10.0`, warm filesystem, no `CLOJURE_LOAD_PATH`.
- Build commands:

```sh
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0 \
  --no-restore

dotnet build Clojure/Clojure.Main/Clojure.Main.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0 \
  --no-restore
```

- Compiled runtime assemblies present in `Clojure.Main/bin/Release/net10.0`: 40 `clojure*.clj.dll` files, including `clojure.core.clj.dll`, `clojure.main.clj.dll`, and `clojure.string.clj.dll`. Source `.clj` files are also present in the output, but final `clojure.core.clj.dll` and `clojure.main.clj.dll` timestamps are newer than their matching source files, so `RT.load` selects the compiled assemblies by default.
- Primary benchmark script: `research/scripts/measure-startup.zsh`.
- Primary command:

```sh
research/scripts/measure-startup.zsh
```

- Primary measured runs, ms: `418.4, 418.1, 425.7, 426.6, 417.8, 420.1, 414.1, 415.2, 420.2, 420.8`.
- Primary result: median `419.2 ms`, p95 `426.6 ms`.
- Secondary command:

```sh
research/scripts/measure-startup.zsh -- \
  dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  -e "(require 'clojure.string) (println (clojure.string/upper-case \"ok\"))"
```

- Secondary measured runs, ms: `440.1, 493.8, 438.5, 435.9, 437.8, 431.3, 436.6, 428.5, 438.4, 438.2`.
- Secondary result: median `438.0 ms`, p95 `493.8 ms`.

The installed branch global tool now passes the `<= 500 ms` median and
`<= 600 ms` p95 thresholds.

## Verification Commands

List the installed global tools and confirm the command shims:

```sh
dotnet tool list --global
command -v clj-mayne
command -v Clojure.Main
```

Compare expression startup through the installed global-tool commands:

```sh
USERPROFILE=$HOME pwsh -NoProfile -Command \
  './scripts/bench-startup.ps1 -Tools @("clj-mayne","Clojure.Main") -Runs 3 -Warmup 1'
```

Measure a script through either installed tool:

```sh
RUNS=3 WARMUPS=1 research/scripts/measure-startup.zsh -- \
  clj-mayne Clojure/Clojure.Samples/clojure/samples/spec_schema.clj
```

Use `RUNS` and `WARMUPS` to override the number of measured runs and warmups.
Pass `-- <command...>` to measure another installed-tool command.

The direct build-output gate is still useful for compiler/runtime development,
but it is not the user-facing installed-tool comparison:

```sh
research/scripts/check-clojure-main-startup-suite.zsh
```

This runs fresh `clojure.main` processes for small expression and script cases
covering macro expansion, destructuring, protocol/deftype generation,
multimethod dispatch, lazy seqs, first library require, `clojure.spec`
validation/instrumentation, the `spec_schema.clj` file target, and small
external-package demos for `Newtonsoft.Json` and `Microsoft.Data.Sqlite`. It
fails if any measured run exceeds `MAX_MS` (default `500`).

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
| 2026-05-22 | `3d17c436` + working tree | Current compiled-runtime startup still had a roughly one-second path; first repeatable baseline needed before optimizing. | Measured the primary command with 2 warmups and 10 timed runs, no `CLOJURE_LOAD_PATH`. | Baseline runs were `950.1, 947.6, 940.6, 946.0, 949.6, 937.4, 943.0, 941.8, 952.6, 945.9`; median `946.0 ms`, p95 `952.6 ms`. | Baseline |
| 2026-05-22 | `3d17c436` + working tree | Default startup was loading `clojure.core.server` even without `clojure.server.*` configuration. | Made `RT.PostBootstrapInit` require and start `clojure.core.server` only when matching environment configuration exists; explicitly kept `clojure.main` preload before macro spec checks are enabled. | Primary runs were `925.7, 924.6, 935.5, 933.5, 981.5, 925.9, 942.4, 933.6, 927.4, 920.0`; median `930.5 ms`, p95 `981.5 ms`. | Kept, insufficient alone |
| 2026-05-22 | `3d17c436` + working tree | The remaining `clojure.main` cost was mostly source-loading or initializing spec support. | Experimentally compiled `clojure.spec.alpha` and `clojure.core.specs.alpha` to `.clj.dll` with macro instrumentation disabled for that compile and copied them into the output. | Primary runs were `588.1, 589.3, 586.3, 593.5, 583.4, 588.1, 591.5, 590.3, 590.5, 590.9`; median `589.8 ms`, p95 `593.5 ms`. | Rejected as incomplete; median still missed and spec self-AOT needs a separate build policy |
| 2026-05-22 | `3d17c436` + working tree | `clojure.main` only needs spec on error-formatting paths, not on successful `-e` startup. | Removed the eager `clojure.spec.alpha` require from `clojure.main`; removed the unconditional `*explain-out*` binding; added lazy `requiring-resolve` for spec explain formatting only when spec error data is printed. | Final primary runs were `418.4, 418.1, 425.7, 426.6, 417.8, 420.1, 414.1, 415.2, 420.2, 420.8`; median `419.2 ms`, p95 `426.6 ms`. Secondary `clojure.string` require median `438.0 ms`, p95 `493.8 ms`. | Kept |

## Test Evidence

Focused AOT regression command:

```sh
dotnet test Clojure/Csharp.Tests/Csharp.Tests.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0 \
  --no-restore \
  --filter AotRegressionTests
```

Result: passed `23`, failed `0`, skipped `2`, total `25`.

Skipped tests:

- `MinimalNamespaceAotPassesIlVerifyWhenConfigured`: skipped because `CLOJURE_AOT_ILVERIFY` was not set to a local `ilverify` executable.
- `ModernPersistedAotEmitsVerifiedPortableDebugSymbols`: skipped because this gate is enabled for Debug builds only.

Additional smoke check after lazy spec loading:

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  -e "(let [x] x)"
```

Result: exited `1` with the expected macro syntax error report using `clojure.core.specs.alpha/bindings`, proving lazy spec resolution still supports spec-backed error formatting.

## Completion Evidence

Do not mark `clojure-clr-qiz` complete until this file records:

- The benchmark command or script used.
- Runtime, SDK, OS, architecture, configuration, and commit.
- At least 10 measured runs for the primary command.
- Median <= 500 ms and p95 <= 600 ms for the primary command.
- Secondary require-path measurement.
- Relevant AOT regression tests passing, or a precise explanation of any tests that could not be run.
