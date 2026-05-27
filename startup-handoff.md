# Startup Optimization Hand-off

This document describes the retained startup optimization work on branch
`isak-aot-spec-first`. It is written as an implementation hand-off, not as a
patch. The affected behavior is startup cost for `Clojure.Main` on modern .NET
target frameworks.

## Retained Scope

The kept startup change set has two parts:

1. Precompile the spec namespaces for modern .NET builds.
2. Add a script that ReadyToRun-compiles generated Clojure namespace DLLs.

## Files

### `Clojure/Clojure.Compile/Clojure.Compile.csproj`

The modern .NET post-build compiler invocation now runs the compiler through
`dotnet "$(TargetPath)"`. This avoids assuming a generated `.exe` launcher or
`mono` for `net9.0`, `net10.0`, and `net11.0`.

Why: the spec precompile step below needs to invoke the just-built compiler on
modern .NET. Running the compiler through `dotnet` matches the framework-
dependent output shape for these target frameworks.

The modern .NET post-build target also compiles these spec namespaces after the
existing core namespace compilation:

- `clojure.spec.alpha`
- `clojure.spec.gen.alpha`
- `clojure.core.specs.alpha`
- `clojure.spec.test.alpha`

That spec compilation runs with `clojure.spec.skip-macros=true`, which is the
existing modern .NET flag name for disabling spec macro instrumentation.

Why: startup pays less interpretation and loading cost when the spec namespaces
already have generated namespace DLLs. The skip flag is needed only for the
compile step itself: it prevents spec's own macro checks from running while spec
is being compiled.

### ReadyToRun helper

This script ReadyToRun-compiles generated Clojure namespace DLLs in a target
output directory. By default it targets:

`Clojure/Clojure.Main/bin/Release/net10.0`

The script:

- derives the target framework from the output directory unless `TFM` is set;
- asks MSBuild for the SDK runtime identifier unless `RID` is set;
- finds the installed `Microsoft.NETCore.App` runtime matching the target
  framework major version;
- locates `crossgen2`, or uses `CROSSGEN2` when provided;
- compiles every `clojure.*.clj.dll` in the target directory;
- replaces the generated DLLs in place after successful compilation.

Why: the generated Clojure namespace DLLs are not covered by the normal project
publish pipeline. Running `crossgen2` over those DLLs after the build moves more
startup work into native-ready images before the command-line tool starts.

## Build Note

The branch also changes the generated namespace DLL copy and embedding paths in
the project files from solution-root-relative paths to paths relative to the
owning project files. This is not a startup optimization by itself; it only made
the command-line verification flow independent of solution-level MSBuild state.

## Startup Measurements

Measurements are medians from the retained build after ReadyToRun generation:

| Scenario | Median |
| --- | ---: |
| `Clojure.Main -e :ok` | 176.3 ms |
| Require `clojure.string` | 194.1 ms |
| Require `clojure.spec.alpha` and validate | 188.1 ms |
| Require `clojure.spec.test.alpha` and instrument | 220.5 ms |
| `stm/teststm.clj` | 232.3 ms |
| `counter.clj` | 233.6 ms |
| `dm-test.clj` | 229.9 ms |
| `deftype/testprotocol.clj` | 220.5 ms |
