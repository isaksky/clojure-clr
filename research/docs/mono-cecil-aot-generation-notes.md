# Mono.Cecil AOT Generation Research Notes

These repos were cloned as shallow working copies on 2026-05-21:

| Repo | Local path | Commit | Why it is here |
| --- | --- | --- | --- |
| XamlX | `../XamlX` | `86f5a26` | Closest example of a compiler-shaped abstraction over Cecil type/method/IL generation. |
| Avalonia | `../Avalonia` | `46137bd` | Production build task that uses XamlX/Cecil to compile source assets into IL inside an assembly. |
| ILRepack | `../il-repack` | `337e569` | Best reference here for whole-assembly mechanics: reading assemblies, creating a target assembly, symbols, strong naming, deterministic output. |
| coverlet | `../coverlet` | `3a241bc` | Best reference here for nontrivial method-body rewriting, branch targets, exception handlers, and macro normalization. |
| Fody | `../Fody` | `691d756` | Useful build-pipeline pattern for read, transform, write with symbols and strong-name handling. |

## Recommended Reading Order

1. XamlX Cecil backend
2. Avalonia build task integration
3. ILRepack assembly writer mechanics
4. coverlet method-body correctness details
5. Fody pipeline shape

For Clojure-CLR AOT, XamlX/Avalonia are the most conceptually relevant. Clojure-CLR currently emits through `System.Reflection.Emit`/`PersistedAssemblyBuilder` in `../../Clojure/Clojure/CljCompiler/Context/MyAssemblyGen.cs` and is coordinated through `../../Clojure/Clojure/CljCompiler/Context/GenContext.cs`. XamlX is useful because it shows how to isolate the compiler-facing type/method/IL API from the Cecil-specific implementation.

## XamlX: Compiler-Facing Cecil Backend

Start with `../XamlX/src/XamlX.IL.Cecil`.

Interesting files:

- `../XamlX/src/XamlX.IL.Cecil/CecilTypeSystem.cs`
  - Lines 48-68: constructs a `CecilTypeSystem`, reads reference assemblies and the target assembly using `AssemblyDefinition.ReadAssembly`, and wires custom resolver/importer providers.
  - Lines 137-147: creates and registers a new assembly with `AssemblyDefinition.CreateAssembly`.
  - Lines 155-163: turns a `TypeDefinition` into a compiler-facing type builder.
  - Lines 165 onward: centralizes adding `CompilerGeneratedAttribute`.

- `../XamlX/src/XamlX.IL.Cecil/CecilTypeBuilder.cs`
  - Lines 26-47: defining fields.
  - Lines 55-87: defining methods, parameters, method bodies, overrides, and compiler-generated metadata.
  - Lines 103-123: defining constructors.
  - Lines 127-144: defining nested types.
  - Lines 183-199: defining generic parameters and adjusting the generated type name.

- `../XamlX/src/XamlX.IL.Cecil/CecilEmitter.cs`
  - Lines 92-98: wraps a Cecil `MethodDefinition` body as an emitter.
  - Lines 140-176: maps higher-level opcodes and operands to Cecil `Instruction.Create(...)`.
  - Lines 185-211: implements labels by deferring branch operands until the target instruction exists.
  - Lines 214-234: defines locals, labels, and local/label emission.

Why this matters for Clojure-CLR:

- It is the cleanest example here of a Cecil backend that hides Cecil from most of the compiler.
- It has a direct analogue to Clojure-CLR's current `CljILGen`/`ILGen` usage: a compiler emits through a stable interface, while the backend translates that into Cecil instructions.
- It handles type references/importing at the boundary, which is one of the recurring hard parts when moving from Reflection.Emit to Cecil.

## Avalonia: Production Build-Time IL Generation

Start with `../Avalonia/src/Avalonia.Build.Tasks/XamlCompilerTaskExecutor.cs`.

Interesting sections:

- Lines 60-64: creates a `CecilTypeSystem` over references plus the target assembly.
- Lines 84-93: writes the modified main assembly and optional reference assembly, including symbols and strong-name key handling.
- Lines 193-216: configures `AvaloniaXamlIlCompiler` with the Cecil-backed type system and emit mappings.
- Lines 445-453: compiles a parsed XAML document into generated methods/types in the target assembly.
- Lines 693 onward: handles reference assembly output.

Why this matters for Clojure-CLR:

- It shows how the Cecil backend gets embedded in a real build task.
- It keeps parsing/transformation separate from final IL emission.
- It has the same shape as namespace AOT: load inputs, resolve types, transform compiler AST, generate methods/types, write an assembly.

## ILRepack: Assembly Artifact Mechanics

Start with `../il-repack/ILRepack/ILRepack.cs`.

Interesting sections:

- Lines 132-180: reads input assemblies with `ReaderParameters`, symbol fallback, assembly resolver registration, and IL-only validation.
- Lines 365-376: creates the target assembly with `AssemblyDefinition.CreateAssembly`.
- Lines 440-459: writes the final assembly using `WriterParameters`, strong-name options, symbol writer provider, deterministic MVID, and deterministic timestamp handling.

Why this matters for Clojure-CLR:

- AOT success is not only "valid IL in method bodies"; it is also a valid PE/metadata artifact.
- This is a good reference for resolver setup, symbol behavior, strong naming, deterministic builds, and final write parameters.
- It is less useful for AST-to-IL design, but very useful once Clojure-CLR has generated a Cecil assembly and needs robust output behavior.

## coverlet: Method Body Correctness

Start with `../coverlet/src/coverlet.core/Instrumentation/Instrumenter.cs`.

Interesting sections:

- Lines 223-250: opens a module with symbols and a resolver.
- Line 360: writes a modified module with symbols.
- Lines 663-849: normalizes method bodies, injects instructions, tracks branch targets, rewrites targets, handles exception handler boundaries, then optimizes macros.
- Lines 1011-1020: small, clear example of inserting instructions before an existing instruction.
- Lines 1024-1068: rewrites branch/switch operands and exception handler boundaries after inserted code changes control flow.

Why this matters for Clojure-CLR:

- Clojure emits rich control flow: `if`, `case`, `try`, `catch`, `finally`, `recur`, closures, and dynamic dispatch.
- This file shows the sort of care needed when generated or rewritten IL has branch targets and exception regions.
- Even if Clojure-CLR generates from scratch instead of rewriting existing methods, the macro/branch/exception-handler discipline is still relevant.

## Fody: Read/Transform/Write Pipeline

Start with `../Fody/FodyIsolated`.

Interesting files:

- `../Fody/FodyIsolated/ModuleReader.cs`
  - Lines 18-40: reads a module with resolver, in-memory mode, and optional symbols.

- `../Fody/FodyIsolated/InnerWeaver.cs`
  - Lines 85-110: overall pipeline: resolve, read module, initialize, execute transforms, add metadata, strong-name, write, dispose.
  - Lines 177-198: executes each transformation.
  - Lines 234-270: adds a marker type/fields to record processing metadata.

- `../Fody/FodyIsolated/ModuleWriter.cs`
  - Lines 5-18: writes with strong-name and symbol settings.

Why this matters for Clojure-CLR:

- Fody is not a direct compiler model, but it is a concise reference for build-time assembly processing structure.
- The pipeline shape is useful if Clojure-CLR AOT is implemented as a transformation step over a partially generated assembly.
- The metadata marker pattern may be useful for identifying compiler-generated assemblies or avoiding duplicate processing.

## Mapping Back To Clojure-CLR

Current Clojure-CLR entry points to compare:

- `../../Clojure/Clojure/CljCompiler/Context/GenContext.cs`
  - Lines 120-145: creates internal or external generation contexts.
  - Lines 157-194: creates `MyAssemblyGen`, module/document writers, and assembly output path.
  - Lines 263-270: centralized assembly save path.

- `../../Clojure/Clojure/CljCompiler/Context/MyAssemblyGen.cs`
  - Lines 61-80: non-persisted `AssemblyBuilder` path.
  - Lines 84-172: persisted assembly constructor paths for .NET Framework and .NET 9+.
  - Lines 192-214: `SaveAssembly`.
  - Lines 217-240: lower-level `PersistedAssemblyBuilder.GenerateMetadata` and `ManagedPEBuilder` path.

- `../../Clojure/Clojure/CljCompiler/Ast/IlGen.cs`
  - Lines 21-34: `CljILGen` currently wraps `System.Reflection.Emit.ILGenerator`.

Likely design lessons:

- If Cecil is introduced, first define the compiler-facing API boundary. XamlX's `IXamlILEmitter` plus `CecilEmitter` is the best reference.
- Keep assembly output concerns separate from expression emission. ILRepack is the useful reference for writer parameters, symbols, deterministic output, and strong naming.
- Treat control-flow emission as a first-class risk area. coverlet is the useful reference for branch targets, switch targets, exception handler boundaries, and macro optimization.
- Avoid letting every AST node depend directly on Cecil. That would make the current Reflection.Emit path harder to keep or compare.

## Practical Questions To Answer Next

- Should Clojure-CLR keep `CljILGen` as the compiler-facing API and add a Cecil-backed implementation, or introduce a smaller backend-neutral emitter interface?
  Answer after paired-generation checkpoint: no Cecil emitter boundary is needed for the next AOT slice. Keep the current Reflection.Emit/`PersistedAssemblyBuilder` path and continue tightening generated-artifact identities unless a feature-specific blocker proves that persisted Reflection.Emit cannot express the required metadata.
- Does AOT need to support both Reflection.Emit/PersistedAssemblyBuilder and Cecil during a transition?
  Answer after paired-generation checkpoint: not yet. Supporting both would add backend replacement work before the remaining dynamic host interop, generated-form, cross-TFM, and debug-symbol issues have demonstrated a need for Cecil.
- How will generated debug information and sequence points be represented?
  Still open. Modern persisted AOT currently disables persisted debug document/PDB emission; `clojure-clr-zkm` tracks verified portable debug symbols.
- How will Clojure-CLR import references for generic methods/types, especially generated closure and function classes?
  Partially answered for current generated artifacts by `GeneratedArtifactRegistry`, paired `GenContext`s, paired `gen-interface`, and runtime-only `gen-delegate` wrappers. Dynamic host interop helpers, `deftype*`/`reify*`, `gen-class`, and `proxy` still need targeted policies.
- What verification tool will be used in CI for generated assemblies?
  The NUnit AOT fixture now covers reflection inspection and source-free fresh-process loading by default, with `dotnet-ilverify` available through `CLOJURE_AOT_ILVERIFY` for an opt-in IL verification gate.
