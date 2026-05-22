# Backend Strategy Decision

## Recommendation

Use a staged `PersistedAssemblyBuilder` dual-generation path first. Do not start by replacing persisted output with Mono.Cecil.

The current minimal AOT sample compiles, source-free loads, and verifies with `dotnet-ilverify`. A standalone dual-generation prototype also emits one runnable dynamic type and one persisted type with matching logical behavior, saves the persisted assembly, loads it in a fresh process, and verifies the saved IL.

That means the immediate blocker is not "PersistedAssemblyBuilder cannot save the minimal assembly." The immediate architectural work is a backend-aware identity map so eval and persisted generation do not share raw `Type`/member objects.

## Reassessment After Paired Generation

Decision: keep the first implementation on Reflection.Emit plus `PersistedAssemblyBuilder`; do not start a Cecil backend for the next AOT slice.

The paired-generation path now covers the original `def`/`defn`/top-level `let` sample, macro/progressive-eval fixtures, source-free fresh-process loading, generated function identity pairing, init/function constants and member records, same-runtime target policy, and the current runtime namespace tranche (`clojure.walk`, `clojure.template`, `clojure.set`, `clojure.string`, and `clojure.data`). Generated-form expansion beyond the original tranche also works without replacing the backend: `gen-interface` is paired across persisted/eval contexts, `gen-delegate` uses a runtime-only wrapper policy so saved namespace DLLs do not reference transient delegate assemblies, `gen-class` saves standalone persisted class assemblies and loads them back for compile-time use, and `proxy` emits persisted proxy classes into the namespace DLL while separate eval preserves immediate execution semantics.

No remaining open AOT beads are feature-specific backend blockers. Explicit-target metadata/reference polish is implemented for the currently verified cases, including custom attributes that need reference-assembly constructors, named property/field blobs, Type-valued source metadata, dynamic host interop helpers, and saved assembly `TargetFrameworkAttribute` metadata.

Introduce Cecil only if one of those slices proves that `PersistedAssemblyBuilder` cannot express the required metadata or cannot produce a valid artifact with acceptable verification. Until then, Cecil remains a design reference for a future backend boundary, resolver/import behavior, symbols, strong naming, deterministic output, and branch/exception-handler discipline.

## PersistedAssemblyBuilder Findings

Works for the first milestone:

- Public generated function classes.
- Static and instance methods.
- Constructors and static constructors.
- Constant fields.
- Namespace init type and `Initialize`.
- Source-free load through `RT.load`.
- IL verification for the minimal saved DLL.

Known constraints:

- Persisted assemblies cannot execute before save/load, so progressive eval still needs a runnable eval counterpart.
- Delegate types and `SetImplementationFlags` remain a sharp edge in `DynInitHelper`.
- Modern persisted namespace AOT emits verified portable debug symbols in Debug builds by using the manual PE/PDB save path when a debug writer is present.
- Target-framework correctness defaults to the executing runtime and can select explicit reference assemblies through `MetadataLoadContext` when requested.
- Explicit-target custom attributes are emitted through encoded metadata blobs so saved assemblies avoid runtime implementation assembly references.

## Cecil Findings

XamlX is the best model for a later backend boundary:

- `CecilTypeSystem` centralizes resolver/importer setup and target assembly registration.
- `CecilTypeBuilder` hides Cecil field/method/constructor/type definitions behind a compiler-facing API.
- `CecilEmitter` maps higher-level opcodes and operands to Cecil instructions, including label fixups.

Avalonia shows how that backend is used in production:

- Build task creates a `CecilTypeSystem` over references and target assembly.
- Compiler components receive type builders rather than raw Cecil objects.
- Output writing handles symbols and strong-name keys.

ILRepack, Fody, and coverlet are useful once a Cecil backend exists:

- ILRepack: resolver setup, deterministic write, symbols, strong naming.
- Fody: concise read/transform/write pipeline and marker metadata.
- coverlet: branch target and exception handler discipline.

## Why Not Cecil First

- The current compiler emits directly through `System.Reflection.Emit` and stores reflection `Type`/member objects throughout AST nodes.
- A Cecil backend cannot reuse `CljILGen` directly without either a backend-neutral IL API or a translation layer for all operand shapes.
- The first milestone can be proven with Reflection.Emit/PersistedAssemblyBuilder if generated identities are paired correctly.
- Starting with Cecil would combine two hard problems: backend replacement and eval/persisted identity mapping.

## Postponed

- Full Cecil backend.
- Newly discovered explicit-target persisted metadata edge cases outside the current verification matrix.
- Async method emission beyond current conditional support.
