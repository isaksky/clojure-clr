# Dual-Generation Prototype

Location:

```text
research/experiments/dual-generation-minimal/
```

## What It Proves

The prototype emits one logical function into two universes:

- Runnable eval universe: `AssemblyBuilderAccess.Run`
- Persisted universe: `PersistedAssemblyBuilder`

The eval version executes immediately and sets a Clojure Var root. The persisted version emits an `Initialize` method that calls the persisted function and binds the same Var root after the saved DLL is loaded.

It is intentionally small. It proves the mechanics of paired generated code, immediate eval execution, persisted initializer replay, source-free load, and IL verification. It does not yet exercise Clojure compiler AST replay.

## Commands

Run emit plus same-process load:

```sh
cd research/experiments/dual-generation-minimal
dotnet run --project DualGenerationMinimal.csproj -p:TargetFrameworks=net10.0
```

Output:

```text
eval-value=42
persisted=/Users/admin/src/clojure-clr/research/experiments/dual-generation-minimal/out/DualGenerationPersisted.dll
persisted-value=42
```

Fresh-process load of the saved assembly:

```sh
cd research/experiments/dual-generation-minimal
dotnet run --project DualGenerationMinimal.csproj -p:TargetFrameworks=net10.0 -- load out/DualGenerationPersisted.dll
```

Output:

```text
persisted-value=42
```

IL verification:

```sh
dotnet tool install --tool-path /tmp/clojure-clr-tools dotnet-ilverify
DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/10.0.105/libexec \
/tmp/clojure-clr-tools/ilverify \
  research/experiments/dual-generation-minimal/out/DualGenerationPersisted.dll \
  -r 'Clojure/Clojure/bin/Debug/net10.0/*.dll' \
  -r '/opt/homebrew/Cellar/dotnet/10.0.105/libexec/shared/Microsoft.NETCore.App/10.0.5/*.dll'
```

Output:

```text
All Classes and Methods in .../DualGenerationPersisted.dll Verified.
```

## Takeaway

The first compiler implementation can use paired Reflection.Emit contexts before introducing Cecil. The compiler now mirrors the prototype shape with an explicit `GenerationContextPair`: one runnable eval `GenContext`, one persisted `GenContext`, and a shared generated-artifact registry. The hard part remains making every compiler reference to generated types and members resolve through the correct side of the pair.
