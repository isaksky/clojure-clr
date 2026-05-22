# Generated Type Inventory

Inventory command:

```sh
rg -n "\b(DefineType|DefinePublicType|DefineNestedType|DefineMethod|DefineConstructor|DefineField|DefineProperty|DefineEvent|DefineMethodOverride|SetCustomAttribute|SetImplementationFlags|GetILGenerator|new CljILGen|\bILGenerator\b|CreateType|CreateTypeInfo|FinishType|MakeDelegateType|DynamicMethod|Expression\.Compile|CompileToMethod|RegisterDuplicateType|FindDuplicateType|RegisterDirectLink|TryGetDirectLink)\b" Clojure/Clojure/CljCompiler Clojure/Clojure/Runtime Clojure/Clojure/Lib -g '*.cs'
```

## Primary Generation Paths

| Path | Feature or trigger | Stage | Runnable during compile? | Persisted DLL needed? | User generated refs? | First milestone |
| --- | --- | --- | --- | --- | --- | --- |
| `Compiler.Compile` | Namespace AOT init type `__Init__$...`, `Initialize`, static constructor, constants helper | Namespace compile finalization plus per-form emit | No for persisted; yes semantically through separate eval | Yes | References generated function classes through constants and initializer IL | Required |
| `Compile1` | Top-level macroexpand, analyze, emit, direct-link registration, separate eval | Per top-level form | Yes, via `DoSeparateEval` re-evaluation on `NET9_0_OR_GREATER` | Yes, emitted into init method | Can register persisted fn type for later static calls | Required |
| `FnExpr.Parse` and `ObjExpr.Compile` | `fn*`, `defn`, compiler-generated fn wrappers such as load thunks and top-level expression wrappers | Analysis creates and finalizes type before `Compile1` eval | Yes; eval re-analyzes into eval context | Yes | Constructors, static `invokeStatic`, `invoke`, constant fields, call-sites, direct-link targets | Required |
| `FnMethod.Emit` | Function arity methods, primitive invoke, direct static invoke, async wrappers | Type emission | Yes in eval universe | Yes | Calls generated static method wrappers on same generated type | Required |
| `ObjExpr` constants/call-sites | Constant fields, keyword lookup fields, protocol cache fields, static ctors | Type emission and namespace finalization | Yes in eval universe | Yes | Field and method references point at enclosing generated type | Required |
| `DynInitHelper` | DLR call-site storage and generated delegate types for dynamic host interop | During expression emit; finalizes helper at context save or fn compile | Yes if host interop happens during eval | Yes if persisted code contains dynamic call-sites | Delegate and `CallSite<T>` type references can include generated delegate types | Supported through active backend helper emission |
| `MethodExpr.EmitDynamicCallPreamble` | Dynamic host method/member interop call-sites | Emit | Yes when evaled | Yes when persisted | Uses generated delegate type and static helper method on current type | Supported through backend-local helper artifacts |
| `StaticInvokeExpr` and `InvokeExpr` | Direct linking to `invokeStatic` | Analysis and emit | Eval must not depend on persisted type | Persisted may reference persisted generated function type | High risk: Var-to-Type map can leak wrong universe | Disable or constrain first |
| Duplicate type map in `Compiler` | Resolve duplicate generated names for `deftype`, `reify`, host lookup | Analysis and type resolution | Yes | Yes | Separate eval/compiler maps already exist, but not a full pair map | Required for first-pass policy; deep support deferred |

## Secondary Generated-Type Paths

| Path | Feature or trigger | Stage | Runnable during compile? | Persisted DLL needed? | User generated refs? | First milestone |
| --- | --- | --- | --- | --- | --- | --- |
| `NewInstanceExpr` | `deftype*`, `reify*` main generated type | Analysis builds methods then calls `ObjExpr.Compile` | Yes; class may be observed immediately | Yes for AOT | Base class, interfaces, fields, constructors, dummy methods, protocol methods | Supported through paired generated type family |
| `NewInstanceExpr.CompileBaseClass` | Abstract base/stub for `deftype*` and `reify*` | Analysis before main type compile | Yes | Paired persisted equivalent needed if persisted main type derives from it | Main generated type derives from generated base class | Supported through paired generated type family |
| `NewInstanceMethod` | Generated implementation methods for `deftype*`/`reify*` | Type emission | Yes | Yes | Method override metadata and explicit interface refs | Supported through backend-aware type/member resolution |
| `GenClass` | `gen-class` | Separate compile action | Yes after immediate save/load of the persisted standalone class assembly | Yes, often as standalone `.dll` or `.exe` | Superclass, interfaces, exposed fields, Var static fields | Supported with standalone save/load policy |
| `GenProxy` | `proxy` | Runtime/eval generation, may register during compile | Yes through separate eval generation | Yes when compiling a namespace that contains proxy forms | Superclass/interfaces and method maps | Supported by emitting persisted proxy type into namespace DLL |
| `GenInterface` | `gen-interface` | Separate compile action | Maybe observed after generation | Yes | Extends interfaces, methods, custom attributes | Supported for persisted AOT through paired persisted/eval context generation and registry records |
| `GenDelegate` | `gen-delegate` wrapper class around `IFn` | Runtime/eval generation | Yes | No saved wrapper type; persisted code calls runtime helper | Delegate signature and wrapper field stay runtime-only | Supported for persisted AOT through runtime-only wrapper generation |
| `MyTypeGen` | Helper wrapper used by `DynInitHelper` | Helper type finalization | Yes if helper is eval-side | Yes if helper is persisted-side | Static call-site fields and methods | Covered by `DynInitHelper` |
| `MyAssemblyGen.MakeDelegateType` | Generic delegate type factory | Helper generation | Yes for run assemblies | Yes for persisted assemblies | Delegate constructor/invoke method implementation flags | Defer unless host interop required |
| `GenContext.AddInternalAssembly` | Dummy type/method used to identify runtime dynamic assemblies | Internal eval-context creation | Yes | No | No user references intended | Required only for direct-link safety |
| Runtime binders `GenerateCreationIL` | Emits binder construction IL into `DynInitHelper` methods | Helper method body emission | Yes | Yes for persisted dynamic sites | Binder fields may embed runtime library references | Covered by `DynInitHelper` |

## Reference Flow Observations

- `ObjExpr.Compile` finalizes function types immediately with `CreateType`; the same analyzed node cannot later be safely rebound to another backend without a member map.
- `DoSeparateEval` resets `CompilerContextVar` to the paired eval context, so eval-side generated artifacts can be recorded without using persisted types.
- `RegisterDirectLink` stores `Var -> Type`, currently a single `Type` value. For paired generation it must become backend-aware or be disabled while compiling persisted code.
- `RegisterDuplicateType` already separates `_compilerTypeMap` and `_evalTypeMap` on non-.NET Framework builds, but this is name-to-Type only and does not map members.
- `DynInitHelper.MakeDelegateType` remains a sharp persisted-assembly edge because delegate `.ctor` and `Invoke` rely on `MethodImplAttributes.Runtime`; current regression coverage verifies the supported dynamic host interop slice.
