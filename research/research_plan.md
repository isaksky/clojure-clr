# Research Plan: Restore ClojureCLR AOT on Modern .NET

## Objective

Gather the information needed to decide and specify the compiler changes required to restore namespace AOT compilation on .NET 9+ while preserving Clojure's progressive load/eval semantics.

The immediate research output should be an implementation-ready audit of generated-type creation and reference flow: which compiler paths create types, when those types must be runnable, when persisted equivalents are needed, and where eval-only and persisted assemblies could accidentally reference each other.

## Local Research Inputs

- `research/Goal.md`
  - Defines the desired .NET 9+ AOT outcome, constraints, first milestone, and acceptance criteria.
- `research/docs/mono-cecil-aot-generation-notes.md`
  - Summarizes relevant Mono.Cecil-based generation patterns and recommends reference reading order.
- `research/XamlX` at `86f5a26`
  - Best reference for a compiler-facing abstraction over Cecil type, method, and IL generation.
- `research/Avalonia` at `46137bd`
  - Production example of using XamlX/Cecil in a build task to generate IL into an assembly.
- `research/il-repack` at `337e569`
  - Reference for whole-assembly read/write mechanics, symbols, strong naming, and deterministic output.
- `research/coverlet` at `3a241bc`
  - Reference for method-body correctness when branches, exception handlers, and instruction operands must be maintained.
- `research/Fody` at `691d756`
  - Reference for a concise read/transform/write pipeline with symbols and strong-name handling.
- `research/cecil` at `882ca5e`
  - Mono.Cecil source reference for resolver, reader, writer, metadata, and symbol behavior.

## Current ClojureCLR Entry Points To Audit

- `Clojure/Clojure/CljCompiler/Compiler.cs`
  - `Compile(...)` creates the external generation context, loader/init type, `Initialize` method, constants initializer, and static constructor.
  - `Compile1(...)` macroexpands, analyzes, emits into the init method, registers direct links for defn-like forms, then on .NET 9+ calls `DoSeparateEval(...)`.
  - `DoSeparateEval(...)` resets compile bindings and evaluates the source form separately to preserve progressive runtime effects.
  - `LoadAssembly(...)` and `InitAssembly(...)` locate and invoke the generated initializer.
- `Clojure/Clojure/CljCompiler/Context/GenContext.cs`
  - Owns internal versus external assemblies, dynamic-init helpers, save coordination, debug documents, and the current `TypeBuilder`.
- `Clojure/Clojure/CljCompiler/Context/MyAssemblyGen.cs`
  - Creates run-only dynamic assemblies and .NET 9+ `PersistedAssemblyBuilder` assemblies.
  - Saves persisted assemblies through either `PersistedAssemblyBuilder.Save` or manual metadata/PE generation.
- `Clojure/Clojure/CljCompiler/Context/DynInitHelper.cs`
  - Creates helper types and delegate types for dynamic call-site initialization.
- `Clojure/Clojure/CljCompiler/Ast/IlGen.cs`
  - `CljILGen` is the current compiler-facing wrapper over `System.Reflection.Emit.ILGenerator`.
- `Clojure/Clojure/CljCompiler/Ast/ObjExpr.cs`
  - Central path for function/reify/deftype-like generated classes, fields, constructors, static constructors, methods, and `CreateType`.
- `Clojure/Clojure/CljCompiler/Ast/FnExpr.cs`
  - Function parsing/analysis, direct-call eligibility, constants, call-site collections, and type compilation.
- `Clojure/Clojure/CljCompiler/Ast/NewInstanceExpr.cs`
  - `deftype*` and `reify*` construction, base-class stub generation, method parsing, and duplicate type registration.
- `Clojure/Clojure/CljCompiler/GenClass.cs`, `GenInterface.cs`, `GenProxy.cs`, `GenDelegate.cs`
  - Secondary generated-type paths that may need separate treatment or explicit first-pass exclusion.
- `Clojure/Clojure/CljCompiler/Ast/MethodExpr.cs`, `StaticInvokeExpr.cs`, `InvokeExpr.cs`, `NewExpr.cs`, host interop expressions
  - Likely cross-reference and direct-linking risk points.

## Research Phases

### 1. Establish The Current AOT Behavior

Build and run the smallest .NET 9+ compile/load scenarios before changing architecture.

Tasks:

- Identify the exact project and command used to compile ClojureCLR for `net9.0`.
- Create or locate a minimal namespace containing:
  - `ns`
  - top-level `def`
  - `defn`
  - top-level function invocation
  - a naked top-level `let`
- Run `(compile 'sample.ns)` and record:
  - Whether the DLL is written.
  - Whether saving fails before load.
  - Whether the saved DLL can be loaded in a fresh process.
  - Whether the generated initializer runs without source.
  - Any invalid cross-assembly references reported by runtime load, `ilverify`, or reflection inspection.
- Repeat with direct linking disabled, if supported by compiler options, to learn whether direct linking is required for the first milestone or can be postponed.

Deliverable:

- `research/results/current-aot-behavior.md` with exact commands, inputs, outputs, failures, and the first observed blocker.

### 2. Build A Generated-Type Inventory

Create a complete map of all compiler paths that call type/member/IL-generation APIs.

Tasks:

- Use `rg` to enumerate every use of:
  - `DefineType`, `DefinePublicType`, `DefineNestedType`
  - `DefineMethod`, `DefineConstructor`, `DefineField`, `DefineProperty`, `DefineEvent`
  - `DefineMethodOverride`, `SetCustomAttribute`, `SetImplementationFlags`
  - `GetILGenerator`, `new CljILGen`, direct `ILGenerator` use
  - `CreateType`, `CreateTypeInfo`, `FinishType`
  - `MakeDelegateType`, `DynamicMethod`, `Expression.Compile`, `CompileToMethod`
  - `RegisterDuplicateType`, `FindDuplicateType`, `RegisterDirectLink`, `TryGetDirectLink`
- For each call site, record:
  - Owning source file and method.
  - Triggering Clojure form or compiler feature.
  - Whether it happens during analysis, emit, eval, finalization, or load.
  - Whether created metadata must be runnable during compilation.
  - Whether it must exist in the persisted DLL.
  - Whether it can reference user generated types.
  - Whether it can be excluded from the first milestone.

Deliverable:

- `research/results/generated-type-inventory.md` with a table covering every generation path.

### 3. Trace Progressive Load/Eval Semantics

Document exactly what must happen after each top-level form is read.

Tasks:

- Trace `Compiler.Compile`, `Compile1`, `Analyze`, `Emit`, `DoSeparateEval`, and `expr.Eval` behavior for representative forms.
- Classify forms into buckets:
  - Pure initializer emission plus separate eval is enough.
  - Analysis itself creates generated runtime types.
  - Emit finalizes generated types before separate eval.
  - Eval requires generated code from the just-analyzed form.
  - Later reading/macroexpansion depends on side effects from the form.
- Pay special attention to:
  - `def`, `defn`, `fn*`, top-level `let*`, `do`, `ns`, `in-ns`
  - macro definitions and macro use in a later form
  - `deftype*`, `reify*`, `gen-class`, `proxy`, dynamic host interop
  - constants, Vars, Keywords, call-sites, and protocol call-sites

Deliverable:

- `research/results/progressive-semantics-map.md` showing the required ordering and which compiler steps are allowed to be replayed into a second backend.

### 4. Identify Eval/Persisted Cross-Contamination Risks

Determine where type references can cross from eval-only generated code into persisted output, or from persisted output into compile-time eval code.

Tasks:

- For each generated type from phase 2, record all references to:
  - Its base type.
  - Implemented interfaces.
  - Field types.
  - Method return and parameter types.
  - Constructor signatures.
  - `newobj`, `call`, `callvirt`, `ldtoken`, `castclass`, `isinst`, `ldsfld`, `stsfld`.
- Trace direct-linking decisions in `StaticInvokeExpr`, `InvokeExpr`, and `RegisterDirectLink`.
- Trace duplicate type lookup for `deftype*`, `reify*`, and generated function classes.
- Inspect how `TypeBuilder.GetMethod`, `TypeBuilder.GetConstructor`, generic constructed types, and call-site delegate types are used.
- Produce a rule for each reference:
  - Runtime library or BCL reference is safe.
  - User/static assembly reference is safe if resolvable.
  - Eval generated type reference is unsafe in persisted output.
  - Persisted generated type reference is unsafe in compile-time eval.
  - Paired eval/persisted generated type reference needs a backend-specific mapping.

Deliverable:

- `research/results/cross-reference-risk-map.md` with concrete unsafe patterns and required mitigation.

### 5. Decide The First Backend Strategy

Compare `PersistedAssemblyBuilder` dual generation against a Cecil-backed persisted backend for the first working version.

Tasks:

- For `PersistedAssemblyBuilder`, test or document:
  - Whether every required metadata construct for the minimal namespace can be emitted.
  - Whether delegate types and `SetImplementationFlags` behavior are valid for saved output.
  - Whether debug documents and sequence points can be saved reliably.
  - Whether reference assembly or target-framework selection is needed immediately.
- For Mono.Cecil, read the relevant reference code:
  - XamlX `CecilTypeSystem`, `CecilTypeBuilder`, and `CecilEmitter`.
  - Avalonia `XamlCompilerTaskExecutor`.
  - ILRepack read/write setup.
  - coverlet branch/exception handling.
  - Fody `ModuleReader`, `InnerWeaver`, `ModuleWriter`.
  - Cecil resolver/import/write internals as needed.
- Compare the two strategies on:
  - Required compiler abstraction work.
  - Ability to replay the existing `CljILGen` emission logic.
  - Reference import/mapping complexity.
  - Debug/symbol support.
  - Verification and artifact robustness.

Deliverable:

- `research/results/backend-strategy-decision.md` recommending one first implementation path and explicitly stating what is postponed.

### 6. Design The Minimal Dual-Generation Contract

Define the smallest abstraction needed to emit one runnable eval version and one persisted version without rewriting the whole compiler.

Tasks:

- Decide whether to extend `GenContext` into paired contexts or introduce an explicit `CompilationBackend`/`TypeEmitter` layer.
- Decide whether `CljILGen` remains the compiler-facing API or whether a smaller backend-neutral IL emitter is required first.
- Define a generated-type identity map:
  - Stable logical type id.
  - Eval `Type`/`TypeBuilder`.
  - Persisted `TypeBuilder` or Cecil `TypeDefinition`.
  - Mapping for fields, methods, constructors, generic instances, and delegate helper types.
- Define when each type is finalized:
  - Eval type finalization before compile-time execution.
  - Persisted type finalization after all persisted references are resolved.
- Define how constants, Vars, Keywords, call-sites, direct links, and dynamic-init helpers are emitted in both universes.
- Define the first-pass exclusion policy for `deftype*`, `reify*`, `gen-class`, `proxy`, async, debug symbols, or direct linking if needed.

Deliverable:

- `research/results/dual-generation-contract.md` with proposed interfaces, data structures, and first-pass exclusions.

### 7. Prototype Outside The Compiler

Create small experiments that isolate risky mechanics before changing compiler internals.

Tasks:

- Emit equivalent simple function classes into:
  - A runnable dynamic assembly.
  - A persisted assembly.
- Execute the eval version immediately and save the persisted version.
- Add an init class with an `Initialize` method that creates Vars and assigns roots.
- Add cross-form behavior:
  - Form 1 defines a Var/function.
  - Form 2 reads or calls it during compilation.
  - The persisted DLL later initializes the same namespace without source.
- Add a generated helper/delegate/call-site case if the minimal namespace requires it.
- Validate with fresh-process load and an IL/metadata verifier.

Deliverable:

- `research/experiments/dual-generation-minimal/` plus `research/results/dual-generation-prototype.md`.

### 8. Define Verification Tooling

Specify how generated assemblies will be checked in development and CI.

Tasks:

- Choose verification commands:
  - Fresh-process load and call generated initializer.
  - Reflection inspection for assembly references and generated type signatures.
  - `ilverify` or equivalent for PE/IL validity.
  - Optional Cecil inspection for method operands that reference generated eval assemblies.
- Define checks for:
  - No persisted reference to the eval assembly.
  - No eval execution dependency on persisted types.
  - Expected initializer type and `Initialize` method exist.
  - Later forms observe earlier compile-time effects.
  - Source-free load succeeds.

Deliverable:

- `research/results/aot-verification-plan.md` with commands and expected outcomes.

### 9. Produce The Compiler Change List

Convert the research into an implementation sequence.

Tasks:

- For each compiler file from phase 2, list required changes and dependencies.
- Separate changes into:
  - Required for minimal `def`/`defn`/`let` milestone.
  - Required for `deftype*`/`reify*`.
  - Required for `gen-class`, `proxy`, `gen-interface`, `gen-delegate`.
  - Required for debug symbols and target-framework support.
  - Required for startup/runtime packaging of compiled core namespaces.
- Include risks and fallback options for each change.

Deliverable:

- `research/results/compiler-change-list.md` suitable for conversion into implementation tasks.

## Research Order

1. Current behavior baseline.
2. Generated-type inventory.
3. Progressive semantics map.
4. Cross-reference risk map.
5. Backend strategy decision.
6. Minimal dual-generation contract.
7. Prototype.
8. Verification plan.
9. Compiler change list.

This order keeps the first milestone grounded in the current compiler before choosing between `PersistedAssemblyBuilder`, Cecil, or a staged approach using both.

## Initial Hypotheses To Test

- The current .NET 9+ path already separates persisted emission from compile-time eval for top-level forms, but it does not yet provide a safe mapping between generated eval types and generated persisted types.
- `ObjExpr.Compile`, `NewInstanceExpr.Build`, and `DynInitHelper` are the highest-risk areas because they create/finalize types during analysis or emit and then expose `Type`, `MethodInfo`, `ConstructorInfo`, and `FieldInfo` objects to later compiler steps.
- Direct linking should probably be disabled or tightly constrained for the first milestone unless the generated-type identity map is already reliable.
- A backend-neutral emitter boundary will likely be needed before a Cecil backend is practical, but the first proof may still be possible with paired Reflection.Emit contexts if all required metadata constructs are supported by `PersistedAssemblyBuilder`.
- `deftype*`, `reify*`, `gen-class`, and `proxy` should be audited immediately but may need explicit first-pass exclusions to keep the minimal namespace milestone achievable.

## Done Criteria For This Research

The research phase is done when it produces enough information to answer:

- Which compiler paths create generated types or generated members?
- Which generated artifacts must run during compilation?
- Which generated artifacts must be persisted?
- Which references are unsafe across eval and persisted universes?
- What abstraction or mapping is needed to emit both versions consistently?
- Which forms are in the first implementation milestone, and which are explicitly deferred?
- What exact tests and verification commands prove that the milestone works?

