# Eval/Persisted Cross-Reference Risk Map

## Rule Set

| Reference kind | Persisted output | Compile-time eval output |
| --- | --- | --- |
| BCL/runtime library type or member | Safe if resolved against the target runtime/reference set | Safe |
| Clojure runtime library type or member | Safe if the generated assembly references the runtime DLL loaded at execution | Safe |
| Previously compiled static user assembly | Safe if resolvable at load time | Safe if loadable during compilation |
| Generated persisted type/member | Safe only inside persisted output or after saved assembly load | Unsafe for progressive eval before save/load |
| Generated eval type/member | Unsafe in saved output | Safe only inside current compile/eval process |
| Paired generated artifact | Safe only when resolved through a backend-specific identity map | Safe only when resolved through the eval side of that map |

## Concrete Unsafe Patterns

| Pattern | Source | Why unsafe | Required mitigation |
| --- | --- | --- | --- |
| Single `Var -> Type` direct-link map | `Compiler.RegisterDirectLink`, `StaticInvokeExpr.Parse`, `InvokeExpr.Parse` | A persisted `Type` can be selected while producing eval code, or an eval type can be selected while producing persisted code if the map is not universe-specific | First pass implemented: suppress direct linking while compiling a `net9.0+` persisted assembly. Later replacement: `Var -> GeneratedTypeId` plus backend-specific method lookup |
| `ObjExpr` stores generated `TypeBuilder`, `CompiledType`, `CtorInfo`, fields | `ObjExpr.Compile`, constructors, constants, call-sites | Emission after finalization can capture member infos from one universe | Introduce generated member pair records for type, fields, ctors, methods |
| `NewInstanceExpr` generated base class | `CompileBaseClass` then main type compile | Persisted main type must derive from persisted base; eval main type must derive from eval base | Pair base and main types under one logical type family |
| Duplicate type lookup by name | `RegisterDuplicateType`, `FindDuplicateType`, `FindDuplicateCompiledType`, host resolution | Name maps cannot answer "which backend do I need?" and cannot map members | Split lookup into eval/persisted maps keyed by logical type id and source name |
| Dynamic call-site delegate generation | `DynInitHelper.MakeDelegateType`, `MethodExpr.EmitDynamicCallPreamble` | `CallSite<T>` embeds generated delegate type `T`; helper fields/methods must stay in same assembly universe | Generate delegate/helper pairs and construct `CallSite<T>` with backend-local delegate type |
| Constructed generic refs with generated args | `typeof(CallSite<>).MakeGenericType`, `TypeBuilder.GetMethod/GetField` | Constructed type identity includes generated delegate/type args | Backend import/map for generic instances and TypeBuilder helper methods |
| `ldtoken` of current/generated type | `NewExpr`, static type expressions | Persisted `ldtoken` to eval type makes saved assembly unloadable; eval `ldtoken` to persisted type cannot run before save/load | Resolve type token through backend map |
| Method bodies call same generated type's static method | `FnMethod.DoEmitStatic` regular `invoke` calls `invokeStatic` | Safe only if both methods are same backend type | Emit method bodies through backend-local method records |
| Constants containing `Type` values | `ObjExpr.EmitValue` and constant fields | Persisting an eval `System.Type` value for generated type bakes a bad runtime dependency or runtime-only object | Represent generated type constants as logical ids and rehydrate per backend |

## Minimal Constants And Member Mapping

The first milestone namespace init and generated function paths now have an explicit audit in `emitted-member-map.md`. The mapped surface includes namespace `Initialize`, init and function `.cctor` methods, `__static_ctor_helper_constants`, `const__N` fields, Var and Keyword constants, generated function constructors, keyword/protocol/static cache fields, and function invoke helpers.

This narrows the immediate unresolved constant risk to constants whose value is itself a generated `System.Type`. Runtime-library constants such as `Var`, `Keyword`, `Symbol`, strings, numbers, persistent collections, and regexes are safe to recreate independently in each backend as long as the generated fields that store them remain backend-local.

## Current Minimal Sample Findings

The current minimal sample generated `sample.ns.clj.dll` references only:

- `Clojure`
- `System.Private.CoreLib`
- `System.ComponentModel.Primitives`

Reflection inspection found only persisted generated types inside the saved assembly, and `dotnet-ilverify` verified all classes and methods. No eval assembly reference was observed for the simple `ns`/`def`/`defn`/call/`let` sample.

## High-Risk Expansion Areas

- `deftype*`/`reify*`: generated base class plus implementation class introduces immediate generated-to-generated inheritance and constructor calls.
- Dynamic host interop: generated delegate types appear inside `CallSite<T>` fields and helper methods.
- Direct linking across forms: useful for performance, but it is the most obvious place where a persisted type can leak into eval or vice versa.
- Debug symbols: sequence points currently reach through `ILGenerator.MarkSequencePoint`; a Cecil backend or manual metadata path needs equivalent source mapping.

## First-Pass Direct-Link Constraint

For `net9.0+` persisted namespace compilation, direct linking is disabled by compiler context, not merely by test environment. Even if `:direct-linking true` is bound, `InvokeExpr` does not produce `StaticInvokeExpr` while the active compiler context targets a `PersistedAssemblyBuilder`, and the direct-link registry does not record or return `Var -> Type` entries in that context. This keeps the minimal AOT path on normal Var invocation until direct links can resolve through backend-specific generated type/member identities.

## Dynamic Host Interop Policy

For `net9.0+` persisted namespace compilation, dynamic host interop emits DLR call-site helper/delegate artifacts through the active backend context. Persisted helper types, `CallSite<T>` fields, helper setter methods, and binder-created initialization methods are saved with the namespace DLL, while the separate eval pass emits runnable counterparts for compile-time execution. Regression coverage checks that persisted dynamic host interop output does not reference transient eval/internal dynamic assemblies.
