# Current AOT Behavior Baseline

Date: 2026-05-21

Host:

- macOS 15.7 arm64
- .NET SDK `10.0.105`
- .NET runtime `10.0.5`
- No .NET 9 runtime installed; `net9.0` apps were run with `DOTNET_ROLL_FORWARD=Major`.

## Build Commands

The runtime library builds for `net9.0` when restore/build is narrowed to that TFM:

```sh
dotnet build Clojure/Clojure/Clojure.csproj -f net9.0 -p:TargetFrameworks=net9.0
```

Result:

```text
Clojure.Source -> .../Clojure.Source/bin/Debug/net9.0/Clojure.Source.dll
Clojure -> .../Clojure/bin/Debug/net9.0/Clojure.dll
Build succeeded.
```

The compile driver also builds, but its custom post-build AOT step fails on this Unix host:

```sh
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj -f net9.0 -p:TargetFrameworks=net9.0
```

First observed project-level blocker:

```text
TargetCmdLine = 'mono .../Clojure.Compile.dll'
mono: command not found
error MSB3073
```

The `net9.0` `Clojure.Compile.dll` artifact is produced before that post-build failure and can be run directly:

```sh
DOTNET_ROLL_FORWARD=Major dotnet ./Clojure.Compile.dll
```

Result:

```text
Runtime: .NET 10.0.5
CLR Version: 10.0.5
```

## Minimal Source

Input file: `research/experiments/current-aot/sample/ns.clj`

```clojure
(ns sample.ns)

(def answer 41)

(defn inc-answer []
  (inc answer))

(def invoked (inc-answer))

(let [x 5]
  (+ x invoked))

(def after-let :loaded)
```

Compile command:

```sh
cd research/experiments/current-aot
mkdir -p out
CLOJURE_COMPILE_PATH="$PWD/out" \
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0/Clojure.Compile.dll sample.ns
```

Output:

```text
Runtime: .NET 10.0.5
CLR Version: 10.0.5
Core: Compiling sample.ns to /Users/admin/src/clojure-clr/research/experiments/current-aot/out -- 56 milliseconds.
```

Generated file:

```text
out/sample.ns.clj.dll
```

## Source-Free Load

Built `Clojure.Main`:

```sh
dotnet build Clojure/Clojure.Main/Clojure.Main.csproj -f net9.0 -p:TargetFrameworks=net9.0
```

Loaded from the output directory, where `sample/ns.clj` is absent:

```sh
cd research/experiments/current-aot/out
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Main/bin/Debug/net9.0/Clojure.Main.dll \
  -e "(require 'sample.ns) (println @#'sample.ns/invoked) (println @#'sample.ns/after-let)"
```

Output:

```text
42
:loaded
```

This proves the saved DLL can be loaded in a fresh process and its generated initializer runs without source for the minimal namespace.

## Direct Linking Disabled

Compile:

```sh
cd research/experiments/current-aot
rm -rf out-direct-link-off
mkdir -p out-direct-link-off
CLOJURE_COMPILE_PATH="$PWD/out-direct-link-off" \
CLOJURE_COMPILER_DIRECT_LINKING=false \
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0/Clojure.Compile.dll sample.ns
```

Source-free load:

```sh
cd research/experiments/current-aot/out-direct-link-off
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Main/bin/Debug/net9.0/Clojure.Main.dll \
  -e "(require 'sample.ns) (println @#'sample.ns/invoked) (println @#'sample.ns/after-let)"
```

Output:

```text
42
:loaded
```

Direct linking is not required for this first minimal milestone.

## Reflection Inspection

Probe:

```sh
cd research/experiments/current-aot
dotnet run --project AssemblyInspector/AssemblyInspector.csproj -- \
  out/sample.ns.clj.dll \
  /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0
```

Assembly references:

```text
Clojure, Version=1.12.3.0, Culture=neutral, PublicKeyToken=cf3caecd327a2fa9
System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e
System.ComponentModel.Primitives, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
```

Generated types:

```text
__Init__$sample$ns
sample.ns$fn__20401
sample.ns$fn__20433
sample.ns$inc_answer
sample.ns$loading__5880__auto____20383
```

No eval-only dynamic assembly reference was visible in the saved DLL metadata.

## IL Verification

Installed the verifier into a temporary tool path and ran it with runtime and Clojure references:

```sh
dotnet tool install --tool-path /tmp/clojure-clr-tools dotnet-ilverify
DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/10.0.105/libexec \
/tmp/clojure-clr-tools/ilverify \
  research/experiments/current-aot/out/sample.ns.clj.dll \
  -r 'Clojure/Clojure.Compile/bin/Debug/net9.0/*.dll' \
  -r '/opt/homebrew/Cellar/dotnet/10.0.105/libexec/shared/Microsoft.NETCore.App/10.0.5/*.dll'
```

Output:

```text
All Classes and Methods in .../sample.ns.clj.dll Verified.
```

## Baseline Conclusion

The smallest namespace requested by the plan works on this machine through the current `PersistedAssemblyBuilder` path when run on .NET 10 with `net9.0` roll-forward. The first observed blocker is not persisted IL generation; it is the `Clojure.Compile.csproj` Unix post-build command using `mono` for modern .NET TFMs. The first compiler-architecture risk remains generated-type identity and cross-reference handling for forms beyond this simple namespace.
