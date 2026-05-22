# AOT Verification Plan

## Local Build

```sh
dotnet build Clojure/Clojure/Clojure.csproj -f net9.0 -p:TargetFrameworks=net9.0
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj -f net9.0 -p:TargetFrameworks=net9.0
dotnet build Clojure/Clojure.Main/Clojure.Main.csproj -f net9.0 -p:TargetFrameworks=net9.0
```

Expected:

- Runtime and main projects build.
- Compile project currently produces `Clojure.Compile.dll` but fails its post-build target on Unix because it invokes `mono` for `net9.0`; fix this before making it a CI gate.

## Minimal Namespace Compile

```sh
cd research/experiments/current-aot
rm -rf out
mkdir -p out
CLOJURE_COMPILE_PATH="$PWD/out" \
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0/Clojure.Compile.dll sample.ns
```

Expected:

- `out/sample.ns.clj.dll` exists.
- Compile exits `0`.

## Fresh-Process Load

```sh
cd research/experiments/current-aot/out
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Main/bin/Debug/net9.0/Clojure.Main.dll \
  -e "(require 'sample.ns) (println @#'sample.ns/invoked) (println @#'sample.ns/after-let)"
```

Expected:

```text
42
:loaded
```

## Direct Linking Off

```sh
cd research/experiments/current-aot
rm -rf out-direct-link-off
mkdir -p out-direct-link-off
CLOJURE_COMPILE_PATH="$PWD/out-direct-link-off" \
CLOJURE_COMPILER_DIRECT_LINKING=false \
DOTNET_ROLL_FORWARD=Major \
dotnet /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0/Clojure.Compile.dll sample.ns
```

Then run the same fresh-process load from `out-direct-link-off`.

Expected:

- Same `42` and `:loaded` output.
- Confirms direct linking is not necessary for the first milestone.

## Metadata Inspection

```sh
cd research/experiments/current-aot
dotnet run --project AssemblyInspector/AssemblyInspector.csproj -- \
  out/sample.ns.clj.dll \
  /Users/admin/src/clojure-clr/Clojure/Clojure.Compile/bin/Debug/net9.0
```

Expected:

- Assembly references contain Clojure and runtime assemblies only.
- No reference name matching eval/internal dynamic assemblies.
- Init type `__Init__$sample$ns` exists.
- Public static `Initialize()` exists.

## IL Verification

```sh
dotnet tool install --tool-path /tmp/clojure-clr-tools dotnet-ilverify
DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/10.0.105/libexec \
/tmp/clojure-clr-tools/ilverify \
  research/experiments/current-aot/out/sample.ns.clj.dll \
  -r 'Clojure/Clojure.Compile/bin/Debug/net9.0/*.dll' \
  -r '/opt/homebrew/Cellar/dotnet/10.0.105/libexec/shared/Microsoft.NETCore.App/10.0.5/*.dll'
```

Expected:

```text
All Classes and Methods ... Verified.
```

## Future CI Gates

- No persisted assembly reference to eval assembly.
- No eval execution dependency on a persisted generated type before save/load.
- Later forms observe earlier compile-time effects.
- Macro definition followed by macro use in same namespace works.
- Generated DLL loads without source.
- `deftype*`/`reify*` tests stay excluded or explicitly fail with a documented unsupported-feature error until implemented.
