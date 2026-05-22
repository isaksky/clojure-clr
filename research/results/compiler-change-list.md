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
| `Context/MyAssemblyGen.cs` | Keep same-runtime persisted save path by default, support explicit reference assembly selection for requested target frameworks, and emit explicit-target custom-attribute blobs for saved assembly/type/member metadata. | Verification plan | Runtime metadata can leak into explicit-target output | Verified by explicit-target metadata/reference regression tests |

## Required For Dynamic Call-Sites

Current policy: dynamic host interop is supported during modern .NET persisted AOT by emitting DLR call-site helpers, generated delegate types, `CallSite<T>` fields, and helper setter methods through the active `GenContext`. The persisted namespace DLL owns persisted helper artifacts, and the separate eval pass owns runnable helper artifacts for compile-time execution.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `Context/DynInitHelper.cs` | Emit helper types, delegate types, call-site fields, and setter methods in the active backend context. | Generated type/member registry | `CallSite<T>` embeds backend-specific delegate type | Supported by persisted dynamic host interop regression |
| `Ast/MethodExpr.cs` | Route dynamic call-site preamble emission through backend-local helper artifacts. | `DynInitHelper` backend context | Invalid generic `CallSite<T>` signatures | Verify persisted output has no eval/internal dynamic references |
| Runtime binders | Ensure `GenerateCreationIL` emits only runtime-library references or backend-local helper refs. | Dynamic call-site pairing | Binder IL can indirectly reference generated delegates | Verify each binder-generated method with `ilverify` |

## Required For `deftype*` / `reify*`

Current policy: `deftype*` and `reify*` are supported during modern .NET persisted AOT by pairing their generated abstract base/stub types, implementation types, constructors, fields, override metadata, and protocol/interface methods across persisted and eval contexts.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `NewInstanceExpr.cs` | Pair generated abstract base class and implementation class. | Type family ids | Persisted implementation deriving from eval base is invalid | Supported by deftype/reify persisted AOT regression |
| `NewInstanceExpr.cs` | Map closed-over fields, constructors, alt constructors, `getBasis`, `create`, dummy methods. | Member pair registry | Constructor calls cross universes | Supported by generated member records |
| `NewInstanceMethod.cs` | Resolve explicit method overrides and implemented interfaces per backend. | Type/member import rules | Override metadata can point at wrong assembly/type | Supported by backend-aware generated type/member resolution |
| `HostExpr.cs` / compiler type resolution | Make duplicate type lookup backend-aware. | Type pair registry | Host resolution may see persisted type during eval | Keep separate `_evalTypeMap` and `_compilerTypeMap`, expand to logical ids |

## Required For `gen-class`, `proxy`, `gen-interface`, `gen-delegate`

Current policy: `gen-interface` is supported during modern .NET persisted AOT by generating the interface into the active persisted context and into the paired eval context, recording the generated interface type and methods in `GeneratedArtifactRegistry`. `gen-delegate` calls are supported as a runtime-only save policy: persisted code calls `GenDelegate.Create`, and the exact delegate wrapper type is generated later at namespace load or function invocation time, so saved namespace DLLs do not reference the transient wrapper assembly. `gen-class` keeps its standalone assembly lifecycle: it saves the persisted class assembly immediately, then loads that DLL back so later compile-time forms can observe a runnable `Type`. `proxy` emits the saved proxy type into the persisted namespace assembly while separate eval generation creates the runnable proxy type needed for progressive evaluation.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `GenClass.cs` | Keep standalone generation, return the loaded saved type for compile-time use, and register it for duplicate-name lookup. | Persisted save path | Generated class assemblies must be available beside the namespace DLL | Supported with source-free load and no eval/internal references |
| `GenProxy.cs` | Allow persisted namespace compile to emit proxy types into the namespace DLL while eval generation remains runnable. | Separate eval pass | Proxy types are needed immediately during compile-time evaluation | Supported with source-free load and no eval/internal references |
| `GenInterface.cs` | Pair generated interface definitions, methods, and custom attributes across persisted/eval contexts. Implemented for protocol-style generated interfaces; registry coverage verifies type and method pairs. | Type pair registry | Interfaces may be referenced by later generated classes | Supported for current runtime namespace tranche |
| `GenDelegate.cs` / `StaticMethodExpr.cs` | Keep wrapper classes runtime-only. Persisted AOT may emit calls to `GenDelegate.Create`; executing the helper during persisted analysis remains guarded. | Runtime helper policy | Delegate reflection shape must remain exact without saving wrapper types | Supported as runtime-only |

## Debug Symbols And Target Frameworks

Current policy: modern persisted namespace AOT uses same-runtime output by default, supports explicit reference assembly selection for requested target frameworks, stamps saved assemblies with target-framework metadata, emits explicit-target custom attributes through metadata blobs, and emits verified portable debug symbols when Debug builds create a debug writer.

| File | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| `GenContext.cs` | Persisted `net9.0+` namespace AOT contexts can emit portable debug documents and sequence points in Debug builds. | Portable PDB verification | Bad sequence points can produce invalid PE/PDB output | Use verified manual PE/PDB save path |
| `MyAssemblyGen.cs` | Keep the manual PE/PDB save path out of first-pass namespace AOT unless an entry point or verified debug writer is explicitly needed. | Verification tooling | Incorrect debug directory or portable PDB row counts | Use simple `PersistedAssemblyBuilder.Save` |
| `MyAssemblyGen.cs` | Target the executing runtime by default, or resolve explicit target reference assemblies through `MetadataLoadContext`. | Reference assembly resolver | Runtime implementation assemblies can leak into explicit-target metadata | Verified for current explicit-target metadata/reference fixtures |

## Runtime Packaging

| Area | Change | Dependency | Risk | Fallback |
| --- | --- | --- | --- | --- |
| Standard namespace compilation | After post-build command fix, compile core namespaces with same verification checks. | Minimal milestone green | Large surface may hit deferred forms | Compile a smaller bootstrap set first |
| NuGet/package layout | Decide whether compiled `.clj.dll` files are embedded resources or copied content for `net9.0+`. | Stable compiled output | Loader path/resource ambiguity | Keep current embedded resource strategy initially |
| Startup | Measure source load vs compiled load after core namespaces compile. | Correctness first | Premature optimization | Delay performance work until verification gates pass |
