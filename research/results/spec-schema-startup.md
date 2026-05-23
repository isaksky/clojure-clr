# `spec_schema.clj` Clojure.Main Startup Benchmark

## Target

This is the current `clojure.main` source-load benchmark required by
[`research/Goal.md`](../Goal.md):

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/spec_schema.clj
```

The measured interval is total wall-clock time for a fresh process to start
ClojureCLR, enter `clojure.main`, and load `spec_schema.clj`. The sample's
normal top-level `sample-results` computation is included; no extra workload
function or REPL expression is invoked.

## Current Evidence

Working tree measurements on 2026-05-22, macOS 15.7 / Darwin 24.6.0 arm64,
.NET SDK 10.0.105 and runtime 10.0.5, Release `net10.0`, warm filesystem, no
`CLOJURE_LOAD_PATH` override:

- Before compiling the spec package namespaces into default loader-visible
  `.clj.dll` files, this benchmark measured:
  `1276.0, 1261.8, 1259.5, 1267.6, 1322.6, 1323.0, 1285.5, 1271.1, 1316.4, 1302.4`;
  median `1280.8 ms`, p95 `1323.0 ms`.
- After adding modern persisted AOT output for `clojure.spec.alpha`,
  `clojure.spec.gen.alpha`, `clojure.core.specs.alpha`, and
  `clojure.spec.test.alpha`, and adding conservative macro-check fast paths for
  valid core and known no-spec `clojure.spec.alpha` macro forms, the benchmark
  measured:
  `771.8, 774.5, 771.8, 758.9, 740.2, 765.4, 761.9, 763.9, 771.3, 771.7`;
  median `768.3 ms`, p95 `774.5 ms`.

This is a material improvement, but it does not satisfy the unchanged
sub-500 ms budget from `Goal.md`.

## Current Findings

The default `Clojure.Main` Release output now contains compiled spec package
namespaces:

- `clojure.spec.alpha.clj.dll`
- `clojure.spec.gen.alpha.clj.dll`
- `clojure.core.specs.alpha.clj.dll`
- `clojure.spec.test.alpha.clj.dll`

The exact sample exits successfully and direct `(require 'clojure.spec.alpha)`
now resolves `clojure.spec.alpha/valid?` from the default output.

The next remaining startup cost is not source-loading those package namespaces.
A local `dotnet-trace` sample after this change still shows substantial time in
compiled namespace static constructors and initialization, especially
`clojure.core`, `clojure.spec.test.alpha`, `clojure.core.specs.alpha`, and
transitive `clojure.pprint` initialization required by `clojure.spec.test.alpha`.

## Verification Commands

```sh
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0

dotnet build Clojure/Clojure.Main/Clojure.Main.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0 \
  --no-restore

research/scripts/measure-spec-schema-startup.zsh
```
