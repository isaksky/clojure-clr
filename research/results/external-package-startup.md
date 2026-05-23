# External Package `clojure.main` Startup Benchmarks

## Targets

These benchmarks exercise first-use startup for common .NET package
interop through `clojure.main`:

```sh
dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/newtonsoft_demo.cljr

dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/sqlite_demo.cljr
```

Both scripts are small source-load startup probes. The measured interval is a
fresh process starting ClojureCLR, entering `clojure.main`, and loading the
script file. No extra workload function, loop, REPL expression, or printing is
added by the benchmark.

## Package Preconditions

The default `Clojure.Main` Release output must include:

- `Newtonsoft.Json.dll`
- `Microsoft.Data.Sqlite.dll`
- `SQLitePCLRaw.batteries_v2.dll`
- `SQLitePCLRaw.core.dll`
- `SQLitePCLRaw.provider.e_sqlite3.dll`
- the SQLite native runtime asset for the host RID, such as
  `runtimes/osx-arm64/native/libe_sqlite3.dylib`

The startup gate uses the same generated-namespace ReadyToRun preparation as
the other startup probes.

## Current Evidence

Working tree measurements on 2026-05-23, macOS 15.7 / Darwin 24.6.0 arm64,
.NET SDK 10.0.105 and runtime 10.0.5, Release `net10.0`, warm filesystem, no
`CLOJURE_LOAD_PATH` override:

- `newtonsoft_demo.cljr` measured runs, ms:
  `321.2, 321.9, 317.3, 318.6, 320.2, 321.6, 325.5, 319.9, 321.4, 324.3`;
  median `321.3 ms`, p95 `325.5 ms`.
- `sqlite_demo.cljr` measured runs, ms:
  `320.4, 324.7, 320.8, 322.4, 319.4, 321.3, 324.3, 310.3, 322.0, 315.6`;
  median `321.1 ms`, p95 `324.7 ms`.

Both targets satisfy the unchanged sub-500 ms startup budget.

The broader startup suite also includes both scripts and passed with
`MAX_MS=500`; the worst external-package measured run was `336.4 ms` for
`file-sqlite-demo`.

## Verification Commands

```sh
dotnet build Clojure/Clojure.Compile/Clojure.Compile.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0

dotnet build Clojure/Clojure.Main/Clojure.Main.csproj \
  -c Release \
  -f net10.0 \
  -p:TargetFrameworks=net10.0

research/scripts/readytorun-generated-clj-dlls.zsh \
  Clojure/Clojure.Main/bin/Release/net10.0

research/scripts/measure-startup.zsh -- \
  dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/newtonsoft_demo.cljr

research/scripts/measure-startup.zsh -- \
  dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll \
  Clojure/Clojure.Samples/clojure/samples/sqlite_demo.cljr

research/scripts/check-clojure-main-startup-suite.zsh
```
