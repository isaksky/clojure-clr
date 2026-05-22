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
| Single `Var -> Type` direct-link map | `Compiler.RegisterDirectLink`, `StaticInvokeExpr.Parse`, `InvokeExpr.Parse` | A persisted `Type` can be selected while producing eval code, or an eval type can be selected while producing persisted code if the map is not universe-specific | Replace with `Var -> GeneratedTypeId` plus backend-specific method lookup, or disable direct linking for first pass |
| `ObjExpr` stores generated `TypeBuilder`, `CompiledType`, `CtorInfo`, fields | `ObjExpr.Compile`, constructors, constants, call-sites | Emission after finalization can capture member infos from one universe | Introduce generated member pair records for type, fields, ctors, methods |
| `NewInstanceExpr` generated base class | `CompileBaseClass` then main type compile | Persisted main type must derive from persisted base; eval main type must derive from eval base | Pair base and main types under one logical type family |
| Duplicate type lookup by name | `RegisterDuplicateType`, `FindDuplicateType`, `FindDuplicateCompiledType`, host resolution | Name maps cannot answer "which backend do I need?" and cannot map members | Split lookup into eval/persisted maps keyed by logical type id and source name |
| Dynamic call-site delegate generation | `DynInitHelper.MakeDelegateType`, `MethodExpr.EmitDynamicCallPreamble` | `CallSite<T>` embeds generated delegate type `T`; helper fields/methods must stay in same assembly universe | Generate delegate/helper pairs and construct `CallSite<T>` with backend-local delegate type |
| Constructed generic refs with generated args | `typeof(CallSite<>).MakeGenericType`, `TypeBuilder.GetMethod/GetField` | Constructed type identity includes generated delegate/type args | Backend import/map for generic instances and TypeBuilder helper methods |
| `ldtoken` of current/generated type | `NewExpr`, static type expressions | Persisted `ldtoken` to eval type makes saved assembly unloadable; eval `ldtoken` to persisted type cannot run before save/load | Resolve type token through backend map |
| Method bodies call same generated type's static method | `FnMethod.DoEmitStatic` regular `invoke` calls `invokeStatic` | Safe only if both methods are same backend type | Emit method bodies through backend-local method records |
| Constants containing `Type` values | `ObjExpr.EmitValue` and constant fields | Persisting an eval `System.Type` value for generated type bakes a bad runtime dependency or runtime-only object | Represent generated type constants as logical ids and rehydrate per backend |

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
