# Backend Strategy Decision

## Recommendation

Use a staged `PersistedAssemblyBuilder` dual-generation path first. Do not start by replacing persisted output with Mono.Cecil.

The current minimal AOT sample compiles, source-free loads, and verifies with `dotnet-ilverify`. A standalone dual-generation prototype also emits one runnable dynamic type and one persisted type with matching logical behavior, saves the persisted assembly, loads it in a fresh process, and verifies the saved IL.

That means the immediate blocker is not "PersistedAssemblyBuilder cannot save the minimal assembly." The immediate architectural work is a backend-aware identity map so eval and persisted generation do not share raw `Type`/member objects.

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
- Debug document support currently forces a manual `GenerateMetadata`/`ManagedPEBuilder` save path.
- Target-framework correctness currently uses the executing runtime's core assembly; cross-TFM/reference-assembly support is not solved.

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
- Cross-target reference assembly selection through `MetadataLoadContext`.
- Rich portable PDB/source-link support.
- `deftype*`, `reify*`, `gen-class`, and `proxy`.
- Async method emission beyond current conditional support.
