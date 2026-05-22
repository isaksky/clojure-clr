# Compiler Change List

## Required For Minimal `def`/`defn`/`let`

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `Clojure.Compile/Clojure.Compile.csproj` | Fix modern .NET Unix post-build command. Use `dotnet $(TargetPath)` or generated apphost, not `mono`, for `net9.0+`. | None | Build pipeline currently fails before standard namespace AOT can run | Bypass post-build in CI and invoke `dotnet Clojure.Compile.dll` directly |
| `Compiler.cs` | Replace `RegisterDirectLink(Var, Type)` with backend-aware direct-link records, or disable direct linking during first paired generation. | Generated type id map | Wrong universe `Type` in static calls | Set `CLOJURE_COMPILER_DIRECT_LINKING=false` for first milestone |
| `Compiler.cs` | Make `Compile1` explicitly emit persisted form and eval form through separate contexts, rather than relying on implicit global context behavior. | Backend context object | Hidden binding leaks | Keep current `DoSeparateEval` for eval side until paired emission exists |
| `ObjExpr.cs` / `FnExpr.cs` | Assign stable logical ids to generated function classes before `ObjExpr.Compile`. | Type pair registry | Type names currently depend on `RT.nextID` and compile mode | Preserve existing names for persisted side and map eval side separately |
| `ObjExpr.cs` | Store constructor, method, and field pairs instead of single `CtorInfo`, `FieldBuilder`, `CompiledType` when emitting both universes from one AST. | Member pair registry | Large surface area | First emit two independent analyses; then collapse to one-analysis replay |
| `StaticInvokeExpr.cs` / `InvokeExpr.cs` | Resolve direct static invokes through backend-local method handles. | Direct-link records | Persisted/eval contamination | Disable direct linking initially |
| `Context/GenContext.cs` | Represent eval and persisted generation contexts as an explicit pair for AOT compile. | `MyAssemblyGen` pair | Current globals make it easy to use the wrong context | Add `CompilationPairContext` without removing existing single context |
| `Context/MyAssemblyGen.cs` | Keep same-runtime persisted save path; add test coverage around `PersistedAssemblyBuilder.Save`. | Verification plan | Cross-target output not solved | Document same-runtime-only first |

## Required For Dynamic Call-Sites

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `Context/DynInitHelper.cs` | Pair helper types, delegate types, call-site fields, and setter methods. | Generated type/member registry | `CallSite<T>` embeds backend-specific delegate type | Exclude dynamic host interop from first milestone |
| `Ast/MethodExpr.cs` | Stop caching raw generated delegate `Type` across backends. | `DynInitHelper` pair | Invalid generic `CallSite<T>` signatures | Emit non-direct reflective fallback for unsupported cases |
| Runtime binders | Ensure `GenerateCreationIL` emits only runtime-library references or backend-local helper refs. | Dynamic call-site pairing | Binder IL can indirectly reference generated delegates | Verify each binder-generated method with `ilverify` |

## Required For `deftype*` / `reify*`

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `NewInstanceExpr.cs` | Pair generated abstract base class and implementation class. | Type family ids | Persisted implementation deriving from eval base is invalid | Explicitly reject during first milestone |
| `NewInstanceExpr.cs` | Map closed-over fields, constructors, alt constructors, `getBasis`, `create`, dummy methods. | Member pair registry | Constructor calls cross universes | Defer |
| `NewInstanceMethod.cs` | Resolve explicit method overrides and implemented interfaces per backend. | Type/member import rules | Override metadata can point at wrong assembly/type | Defer |
| `HostExpr.cs` / compiler type resolution | Make duplicate type lookup backend-aware. | Type pair registry | Host resolution may see persisted type during eval | Keep separate `_evalTypeMap` and `_compilerTypeMap`, expand to logical ids |

## Required For `gen-class`, `proxy`, `gen-interface`, `gen-delegate`

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `GenClass.cs` | Decide whether it remains a separate save pipeline or joins paired AOT backend. | Backend abstraction | Standalone class output has different lifecycle | Defer and document unsupported in first pass |
| `GenProxy.cs` | Separate runtime proxy generation from persisted proxy generation. | Type pair registry | Proxy types are usually needed immediately | Runtime-only for first pass |
| `GenInterface.cs` | Pair generated interface definitions and custom attributes. | Type pair registry | Interfaces may be referenced by later generated classes | Defer |
| `GenDelegate.cs` | Pair wrapper class generation or keep runtime-only. | Type pair registry | Delegate reflection shape must remain exact | Runtime-only for first pass |

## Debug Symbols And Target Frameworks

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `GenContext.cs` | Abstract sequence point emission away from raw `ILGenerator.MarkSequencePoint`. | Backend-neutral debug API | Cecil/manual metadata backend needs a different model | Disable debug symbols for first milestone |
| `MyAssemblyGen.cs` | Make manual PE/PDB save deterministic and tested. | Verification tooling | Incorrect debug directory or portable PDB row counts | Use simple `PersistedAssemblyBuilder.Save` when no PDB/entry point |
| `MyAssemblyGen.cs` | Add target reference assembly policy. | `MetadataLoadContext` or same-runtime policy | Output may bind to executing runtime instead of intended TFM | Same-runtime-only first |

## Runtime Packaging

| Area | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| Standard namespace compilation | After post-build command fix, compile core namespaces with same verification checks. | Minimal milestone green | Large surface may hit deferred forms | Compile a smaller bootstrap set first |
| NuGet/package layout | Decide whether compiled `.clj.dll` files are embedded resources or copied content for `net9.0+`. | Stable compiled output | Loader path/resource ambiguity | Keep current embedded resource strategy initially |
| Startup | Measure source load vs compiled load after core namespaces compile. | Correctness first | Premature optimization | Delay performance work until verification gates pass |
