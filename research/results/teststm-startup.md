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

## Findings

The sample itself is not doing STM work at load time. A `/dev/null` script and
`-e nil` both measure about 420 ms on this machine, while a file containing only
`(ns sample.ns-only)` measures about 1.0 s with default macro spec checks. That
means the extra cost is first macro expansion/spec initialization, not `f1`.

The default path remains above the sub-500 ms target:

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

`clojure.spec.skip-macros=true` already provided the Clojure-compatible opt-out
for macro spec instrumentation, but the portable uppercase spelling
`CLOJURE_SPEC_SKIP_MACROS=true` was ignored on non-Mono .NET builds. Before the
runtime env-var fix, attempting to use the uppercase spelling still measured
about 1.02 s for this target.

After the runtime env-var fix, the portable opt-out path meets the sub-500 ms
target:

```text
$ SKIP_MACRO_SPECS=true research/scripts/measure-teststm-startup.zsh
run,ms
1,470.7
2,475.4
3,472.3
4,476.2
5,473.7
6,476.9
7,477.6
8,479.0
9,474.1
10,478.1
median_ms=475.8
p95_ms=479.0
```

## Change

`RT` now reads both the JVM-style dotted spec environment variables and the
portable uppercase names:

- `clojure.spec.skip-macros`
- `CLOJURE_SPEC_SKIP_MACROS`
- `clojure.spec.check-asserts`
- `CLOJURE_SPEC_CHECK_ASSERTS`

The dotted names keep precedence on non-Mono .NET. Default behavior is unchanged:
macro spec checks remain enabled unless one of the skip variables is set to
`true`.

## Semantics Notes

The sub-500 ms result depends on the existing macro-spec opt-out. That does not
change successful source load behavior for this sample, and invalid macro forms
still fail. The error source changes from spec-backed validation to the macro's
own validation, matching the purpose of `clojure.spec.skip-macros`.

Smoke checks after the change:

```text
$ dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll -e "(let [x] x)"
Syntax error macroexpanding clojure.core/let at (REPL:1:2).
[x] - failed: even-number-of-forms? at: [:bindings] spec: :clojure.core.specs.alpha/bindings

$ CLOJURE_SPEC_SKIP_MACROS=true dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll -e "(let [x] x)"
Syntax error macroexpanding let at (REPL:1:2).
let requires an even number of forms in binding vector in user:1
```

## Remaining Work

If the target is interpreted as requiring default macro spec checks, the goal is
not complete: default startup plus `teststm.clj` load is still about 1.03 s. A
semantics-preserving default-path fix would need either fast compiled spec
support for `clojure.spec.alpha` and `clojure.core.specs.alpha`, or a different
macro-spec checking strategy that avoids loading and running the full spec stack
for successful macro expansions.
