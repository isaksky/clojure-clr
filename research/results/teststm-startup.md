# `teststm.clj` Clojure.Main Startup Benchmark

## Target

This benchmark is the initial `clojure.main` source-load target from
[`research/Goal.md`](../Goal.md):

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/stm/teststm.clj
```

The measured interval is total wall-clock time for a fresh ClojureCLR process
to start, enter `clojure.main`, and load `teststm.clj`. The benchmark does not
invoke `f1` or any other workload function. The sleep call inside `f1` is only
inside a function definition and is not executed during source load.

## Environment

- Date: 2026-05-23.
- OS/architecture: macOS 15.7, Darwin 24.6.0, arm64.
- SDK/runtime: .NET SDK 10.0.105, host/runtime 10.0.5.
- Configuration: Release `net10.0`.
- Build commands:

```sh
dotnet build Clojure/Clojure.Main/Clojure.Main.csproj \
  -c Release \
  -p:TargetFrameworks=net10.0

dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj \
  -c Release \
  -p:TargetFrameworks=net10.0
```

- Timing harness: [`measure-teststm-startup.zsh`](../scripts/measure-teststm-startup.zsh)
  using 2 warmups and 10 measured fresh processes.

## Baseline Findings

The sample itself is not doing STM work at load time. A `/dev/null` script and
`-e nil` both measure about 420 ms on this machine, while a file containing only
`(ns sample.ns-only)` measures about 1.0 s with default macro spec checks. That
means the extra cost is first macro expansion/spec initialization, not `f1`.

Before the core macro-spec fast path, the default macro-spec-enabled path was
above the sub-500 ms target:

```text
$ research/scripts/measure-teststm-startup.zsh
run,ms
1,1014.1
2,1013.8
3,1018.3
4,1018.1
5,1014.5
6,1024.5
7,1017.6
8,1009.8
9,1010.0
10,1031.9
median_ms=1016.0
p95_ms=1031.9
```

The first checked macro form was paying to load `clojure.spec.alpha` and
`clojure.core.specs.alpha` so `clojure.spec.alpha/macroexpand-check` could
validate the form. A direct stopwatch around `Compiler.EnsureMacroCheck()`
measured about 575-585 ms after `Clojure.Main` was already initialized.

## Changes

Two startup changes are now in place:

- `RT` reads both the JVM-style dotted spec environment variables and portable
  uppercase names:
  - `clojure.spec.skip-macros`
  - `CLOJURE_SPEC_SKIP_MACROS`
  - `clojure.spec.check-asserts`
  - `CLOJURE_SPEC_CHECK_ASSERTS`
- `Compiler.CheckSpecs` has a conservative fast path for common
  `clojure.core` macros before spec has been loaded. It returns only when it can
  cheaply prove a covered macro call conforms to the same core macro spec shape,
  or when `clojure.core.specs.alpha` has no macro spec for that core macro. It
  falls back to the existing `clojure.spec.alpha/macroexpand-check` path for
  invalid, unsupported, non-core, or already-spec-loaded cases.

The covered spec-checked forms are the simple `ns`, `defn`, `defn-`, `fn`, and
`let` shapes used by this benchmark. The no-spec core macros encountered while
loading this sample are `and`, `dosync`, `future`, `loop`, `sync`, and
`with-loading-context`.

## After Timing

After the fast path, the default macro-spec-enabled target meets the sub-500 ms
budget:

```text
$ research/scripts/measure-teststm-startup.zsh
run,ms
1,474.3
2,480.0
3,474.7
4,477.7
5,475.0
6,477.1
7,478.5
8,470.5
9,467.0
10,483.7
median_ms=476.1
p95_ms=483.7
```

The lower-bound `/dev/null` script measurement from the same rebuilt output:

```text
$ research/scripts/measure-startup.zsh -- \
  dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll /dev/null
run,ms
1,394.3
2,410.9
3,410.4
4,403.7
5,393.3
6,388.8
7,391.8
8,407.2
9,385.2
10,397.4
median_ms=395.9
p95_ms=410.9
```

The opt-out path remains sub-500 ms but is no longer required for this target:

```text
$ SKIP_MACRO_SPECS=true research/scripts/measure-teststm-startup.zsh
run,ms
1,453.1
2,467.4
3,477.0
4,480.5
5,471.4
6,477.9
7,483.0
8,476.9
9,477.2
10,467.4
median_ms=476.9
p95_ms=483.0
```

## Semantics Notes

The default sub-500 ms result does not use `clojure.spec.skip-macros`; macro
spec checks remain enabled.

The fast path is intentionally conservative:

- If a simple covered core macro form is valid, source loading proceeds without
  loading the full spec stack.
- If a covered core macro form is invalid, the code falls back to the existing
  spec checker and emits the same spec-backed error style.
- If a covered macro uses an unsupported but valid shape, such as destructuring
  in `let`, the code falls back to the existing spec checker and succeeds.
- If `clojure.spec.alpha` has already been loaded, the fast path is disabled and
  the existing spec checker handles the form.

Smoke checks after the change:

```text
$ dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll -e "(let [x] x)"
Syntax error macroexpanding clojure.core/let at (REPL:1:2).
[x] - failed: even-number-of-forms? at: [:bindings] spec: :clojure.core.specs.alpha/bindings

$ CLOJURE_SPEC_SKIP_MACROS=true dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll -e "(let [x] x)"
Syntax error macroexpanding let at (REPL:1:2).
let requires an even number of forms in binding vector in user:1
```

Additional regression coverage:

```sh
dotnet test Clojure/Csharp.Tests/Csharp.Tests.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0 \
  --no-restore \
  --filter AotRegressionTests
```

Result: passed `25`, failed `0`, skipped `2`, total `27`.

The skipped tests were the existing opt-in ILVerify and Debug portable-PDB gates.
