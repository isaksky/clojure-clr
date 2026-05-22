# Compiler Change List

## Required For Minimal `def`/`defn`/`let`

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `Clojure.Compile/Clojure.Compile.csproj` | Fix modern .NET Unix post-build command. Use `dotnet $(TargetPath)` or generated apphost, not `mono`, for `net9.0+`. | None | Build pipeline currently fails before standard namespace AOT can run | Bypass post-build in CI and invoke `dotnet Clojure.Compile.dll` directly |
| `Compiler.cs` | Replace `RegisterDirectLink(Var, Type)` with backend-aware direct-link records. First-pass guard now disables direct linking during `net9.0+` persisted assembly compilation. | Generated type id map | Wrong universe `Type` in static calls | Keep persisted AOT on dynamic Var invocation until backend-aware direct links exist |
| `Compiler.cs` | Make `Compile1` explicitly emit persisted form and eval form through separate contexts, rather than relying on implicit global context behavior. | Backend context object | Hidden binding leaks | Keep current `DoSeparateEval` for eval side until paired emission exists |
| `ObjExpr.cs` / `FnExpr.cs` | Assign stable logical ids to generated function classes before `ObjExpr.Compile`. | Type pair registry | Type names currently depend on `RT.nextID` and compile mode | Preserve existing names for persisted side and map eval side separately |
| `ObjExpr.cs` | Store constructor, method, and field pairs instead of single `CtorInfo`, `FieldBuilder`, `CompiledType` when emitting both universes from one AST. | Member pair registry | Large surface area | First emit two independent analyses; then collapse to one-analysis replay |
| `Compiler.cs` / `ObjExpr.cs` / `DefExpr.cs` / `VarExpr.cs` / `KeywordExpr.cs` | Map namespace init and function class constants, Vars, Keywords, fields, constructors, static constructors, and helper methods through generated member records. Current minimal path records these artifacts; see `emitted-member-map.md`. | Member pair registry | Missing member identities force later replay code back to raw `FieldBuilder`/`ConstructorInfo` handles | Keep two independent analyses and verify registry coverage before one-analysis replay |
| `StaticInvokeExpr.cs` / `InvokeExpr.cs` | Resolve direct static invokes through backend-local method handles. First-pass guard makes `InvokeExpr` ignore requested direct linking in persisted compilation contexts. | Direct-link records | Persisted/eval contamination | Keep direct linking disabled for persisted AOT initially |
| `Context/GenContext.cs` | Represent eval and persisted generation contexts as an explicit pair for AOT compile. | `MyAssemblyGen` pair | Current globals make it easy to use the wrong context | Add `CompilationPairContext` without removing existing single context |
| `Context/MyAssemblyGen.cs` | Keep same-runtime persisted save path; add test coverage around `PersistedAssemblyBuilder.Save`. | Verification plan | Cross-target output not solved | Document same-runtime-only first |

## Required For Dynamic Call-Sites

First-pass policy: dynamic host interop is rejected during modern .NET persisted AOT compilation. `MethodExpr.EmitDynamicCallPreamble` checks the current compiler context before generating DLR call-site helpers, generated delegate types, `CallSite<T>` fields, or helper setter methods. This keeps saved namespace assemblies from containing unpaired dynamic helper artifacts while the minimal AOT path is being validated.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `Context/DynInitHelper.cs` | Pair helper types, delegate types, call-site fields, and setter methods. | Generated type/member registry | `CallSite<T>` embeds backend-specific delegate type | First-pass guard excludes dynamic host interop from modern persisted AOT |
| `Ast/MethodExpr.cs` | Stop caching raw generated delegate `Type` across backends. Current first-pass guard rejects persisted AOT dynamic call-sites before helper/delegate emission. | `DynInitHelper` pair | Invalid generic `CallSite<T>` signatures | Keep persisted AOT rejection until helper artifacts are paired |
| Runtime binders | Ensure `GenerateCreationIL` emits only runtime-library references or backend-local helper refs. | Dynamic call-site pairing | Binder IL can indirectly reference generated delegates | Verify each binder-generated method with `ilverify` |

## Required For `deftype*` / `reify*`

First-pass policy: `deftype*` and `reify*` are rejected during modern .NET persisted AOT compilation. Their generated abstract base/stub type, implementation type, constructors, fields, override metadata, and protocol/interface methods must be paired as one backend-aware type family before these forms can be saved safely.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `NewInstanceExpr.cs` | Pair generated abstract base class and implementation class. Current first-pass guard rejects `deftype*` and `reify*` in persisted AOT contexts. | Type family ids | Persisted implementation deriving from eval base is invalid | Explicitly reject during first milestone |
| `NewInstanceExpr.cs` | Map closed-over fields, constructors, alt constructors, `getBasis`, `create`, dummy methods. | Member pair registry | Constructor calls cross universes | Defer |
| `NewInstanceMethod.cs` | Resolve explicit method overrides and implemented interfaces per backend. | Type/member import rules | Override metadata can point at wrong assembly/type | Defer |
| `HostExpr.cs` / compiler type resolution | Make duplicate type lookup backend-aware. | Type pair registry | Host resolution may see persisted type during eval | Keep separate `_evalTypeMap` and `_compilerTypeMap`, expand to logical ids |

## Required For `gen-class`, `proxy`, `gen-interface`, `gen-delegate`

First-pass policy: `gen-class`, `proxy`, `gen-interface`, and `gen-delegate` are rejected during modern .NET persisted AOT compilation. The current generators either save through separate assembly lifecycles or create runtime-observable wrapper/proxy types, so the persisted namespace path must not include them until each generator has a backend-aware pairing or a documented runtime-only representation.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `GenClass.cs` | Decide whether it remains a separate save pipeline or joins paired AOT backend. Current first-pass guard rejects `gen-class` in persisted AOT contexts. | Backend abstraction | Standalone class output has different lifecycle | Defer and document unsupported in first pass |
| `GenProxy.cs` | Separate runtime proxy generation from persisted proxy generation. Current first-pass guard rejects `proxy` class generation in persisted AOT contexts. | Type pair registry | Proxy types are usually needed immediately | Runtime-only for first pass |
| `GenInterface.cs` | Pair generated interface definitions and custom attributes. Current first-pass guard rejects `gen-interface` in persisted AOT contexts. | Type pair registry | Interfaces may be referenced by later generated classes | Defer |
| `GenDelegate.cs` | Pair wrapper class generation or keep runtime-only. Current first-pass guard rejects `gen-delegate` calls in persisted AOT contexts. | Type pair registry | Delegate reflection shape must remain exact | Runtime-only for first pass |

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
