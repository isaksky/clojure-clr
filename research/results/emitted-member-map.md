# Emitted Constants And Member Map

## Scope

This audit covers the first AOT milestone path for namespace init classes and generated function classes. It traces constants, Vars, Keywords, fields, constructors, static constructors, and helper methods that are emitted by:

- `Compiler.Compile` and `Compile1`
- `ObjExpr.Compile`
- `FnExpr.Parse`
- `FnMethod.Emit`
- `DefExpr`, `VarExpr`, `TheVarExpr`, and `KeywordExpr`

Deferred generator families are listed at the end because they still need their own policy decisions before the first milestone expands to them.

## Registry Rule

Every emitted artifact that can be referenced by generated IL in the minimal path should have one logical identity and backend-local handles in `GeneratedArtifactRegistry`.

Runtime-library objects such as `Var`, `Keyword`, `Symbol`, and persistent collections are safe to recreate independently in each backend. The fields that hold them are generated artifacts and must be registered per backend. Raw generated `Type`, `FieldInfo`, `MethodInfo`, and `ConstructorInfo` values must not cross from eval output to persisted output or the reverse.

## Namespace Init Class

| Artifact | Source | Current mapping | Cross-backend risk |
| --- | --- | --- | --- |
| Init type `__Init__$...` | `Compiler.Compile` | Registered with `GeneratedArtifacts.RegisterTypeBuilder` using the persisted context | Persisted-only; eval does not need an init type for progressive execution |
| `Initialize` method | `Compiler.Compile` | Registered as `GeneratedMemberKind.Method` with logical name `Initialize` | Safe when emitted in persisted init type |
| Constant fields `const__N` | `ObjExpr.EmitConstant` | Registered as `GeneratedMemberKind.Field` with logical name `const__N` | Safe for Vars/Keywords/runtime constants; generated `Type` constants still need logical type-id handling before expansion |
| Constants helper `__static_ctor_helper_constants` | `ObjExpr.DefineConstantFieldInitMethod` | Registered as `GeneratedMemberKind.Method` | Safe; called by the init type static constructor in the same backend |
| Static constructor `.cctor` | `Compiler.Compile` | Registered as `GeneratedMemberKind.StaticConstructor` | Safe; calls `Compiler.PushNS`, the constants helper, then pops bindings |
| Var constants | `RegisterVar`, `DefExpr.Emit`, `VarExpr.Emit`, `TheVarExpr.Emit` | Vars are entered into `VarsVar`, backed by `RegisterConstant`, then emitted through `const__N` fields | Safe as runtime objects resolved by namespace/name through `RT.var` during constant initialization |
| Keyword constants | `RegisterKeyword`, `KeywordExpr.Emit` | Keywords are entered into `KeywordsVar`, backed by `RegisterConstant`, then emitted through `const__N` fields | Safe as runtime objects recreated through `RT.keyword` |

## Generated Function Classes

| Artifact | Source | Current mapping | Cross-backend risk |
| --- | --- | --- | --- |
| Function type | `FnExpr.Parse`, `ObjExpr.Compile` | Declared before type creation and registered when the backend `TypeBuilder` is defined | Safe for two-analysis pairing when logical names match after `RT.nextID` normalization |
| Main public constructor `.ctor` | `ObjExpr.EmitConstructor` | Registered after constructor emission | Safe when constructor calls target backend-local base/runtime constructors |
| Alt/non-meta constructors `.ctor` | `ObjExpr.EmitFieldOnlyConstructors`, `EmitNonMetaConstructor` | Registered with separate `.ctor` member ordinals | Safe; member ordinal distinguishes overloads |
| Static constructor `.cctor` | `ObjExpr.DefineStaticConstructor` | Registered as `GeneratedMemberKind.StaticConstructor` | Safe; initializes same-backend constants and call-site fields |
| Constant fields `const__N` | `ObjExpr.EmitConstant` | Registered as generated fields | Safe for runtime constants; generated `Type` constants need the same logical-id rule as init constants |
| Keyword lookup fields `__site__N__`, `__thunk__N__` | `ObjExpr.EmitKeywordCallsiteDefs` | Registered as generated fields | Safe when keyword call-site fields stay on the same backend function type |
| Protocol cache fields `__cached_class__N` | `ObjExpr.EmitProtocolCallsites` | Registered as generated fields | Safe as runtime `Type` cache fields, but protocol expansion should still be verified separately |
| Meta field `__meta` | `ObjExpr.Compile` | Registered as a generated field | Safe; instance field on backend-local function type |
| Closed-over fields | `ObjExpr.EmitClosedOverFields` | Registered by local binding name | Safe when all constructor and field references are emitted against the same backend type |
| Constants helper `__static_ctor_helper_constants` | `ObjExpr.DefineConstantFieldInitMethod` | Registered as a generated method when used for a type | Safe; same-backend static constructor/helper relation |
| Function methods `invoke`, `invokeStatic`, `invokePrim`, `doInvoke`, `HasArity`, `getRequiredArity` | `FnMethod.Emit`, `FnExpr.EmitMethods` | Registered through `ObjExpr.DefineGeneratedMethod` or explicit registration | Safe when method bodies call backend-local methods on the same generated type |
| Meta methods `meta`, `withMeta` | `ObjExpr.EmitMetaFunctions` | Registered as generated methods | Safe when constructor references resolve to the same backend function type |

## First-Pass Gaps

- Constants whose values are generated `System.Type` instances are still risky. `ObjExpr.EmitValue` can emit `ldtoken` for a `Type` during compilation; if that `Type` is generated in the other backend, the saved assembly can be contaminated. The minimal sample does not hit this path.
- Dynamic host interop remains deferred. `DynInitHelper` creates helper types, delegate types, call-site fields, and setter methods that are not yet registered as paired logical artifacts.
- `MethodExpr.EmitDynamicCallPreamble` defines backend-local interop lambda methods directly on `context.TB`; those helper methods need generated-member identities if dynamic host interop enters the milestone.
- `NewInstanceExpr` and `NewInstanceMethod` still define `deftype*`/`reify*` base and implementation constructors, fields, `getBasis`, `create`, dummy methods, and overrides without the minimal function-class mapping policy.
- `GenClass`, `GenProxy`, `GenInterface`, and `GenDelegate` remain separate generator families and should stay outside the first milestone until their backend policy is explicit.

## Verification

`AotRegressionTests.MinimalNamespaceAotRecordsConstantsVarsKeywordsConstructorsFieldsAndHelpers` now checks the minimal persisted AOT path records:

- namespace init `Initialize`, `.cctor`, constants helper, and `const__N` fields
- Var-backed and Keyword-backed init constants
- generated function `.ctor`, `.cctor`, `invokeStatic`, `invoke`, `HasArity`, and Var-backed constant fields

This does not prove one-analysis replay yet. It does lock down the current two-analysis registry surface so later backend-aware replay work has stable member identities to target.
