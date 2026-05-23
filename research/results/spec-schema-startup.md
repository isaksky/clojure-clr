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
- After adding the missing no-spec fast path for `clojure.core/or`, making
  `clojure.spec.test.alpha` avoid eager `clojure.pprint` and generator loading
  on the normal `instrument` path, and replacing reflection `InvokeMember`
  initializer calls with direct initializer delegates, the IL-only generated
  namespace DLL path still measured about `619.3 ms` median / `639.2 ms` p95.
- After ReadyToRun-compiling the generated `clojure.*.clj.dll` namespace
  assemblies in the default `Clojure.Main` output, the same benchmark command
  measured:
  `306.5, 303.2, 307.3, 307.5, 307.6, 301.3, 334.0, 309.1, 304.0, 302.1`;
  median `306.9 ms`, p95 `334.0 ms`.

The unchanged sub-500 ms budget from `Goal.md` is satisfied only when the
generated namespace DLLs are prepared as ReadyToRun images. Set
`SKIP_CLJ_R2R=true` when running the script to measure the IL-only path.

## Current Findings

The default `Clojure.Main` Release output now contains compiled spec package
namespaces:

- `clojure.spec.alpha.clj.dll`
- `clojure.spec.gen.alpha.clj.dll`
- `clojure.core.specs.alpha.clj.dll`
- `clojure.spec.test.alpha.clj.dll`

The benchmark script prepares those generated namespace assemblies with
[`readytorun-generated-clj-dlls.zsh`](../scripts/readytorun-generated-clj-dlls.zsh)
before timing. The measured command remains:

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/spec_schema.clj
```

The exact sample exits successfully and direct `(require 'clojure.spec.alpha)`
resolves `clojure.spec.alpha/valid?` from the default output.

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

The measurement script requires the local .NET SDK crossgen2 pack for the host
RID. If it is missing, run a ReadyToRun publish once or set `CROSSGEN2` to the
tool path.
