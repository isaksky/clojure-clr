# Minimal Dual-Generation Contract

## Direction

Keep `CljILGen` and Reflection.Emit for the first implementation path, but stop passing raw generated `Type`, `FieldInfo`, `MethodInfo`, and `ConstructorInfo` across backend boundaries. Introduce a small generated-artifact identity layer first. A Cecil backend can later implement the same logical contract.

The first code implementation keeps the existing two-analysis behavior: persisted compilation analyzes and emits the saved assembly, then separate eval re-analyzes the form so progressive load/eval effects happen in runnable code. `GenerationContextPair` carries the persisted `GenContext`, the runnable eval `GenContext`, and their shared `GeneratedArtifactRegistry`. `GeneratedArtifactRegistry` therefore assigns ordinals per backend pass. If the persisted and eval passes declare the same logical artifacts in the same order, they resolve to the same logical `GeneratedTypeId`/`GeneratedMemberId` while retaining separate backend-local handles.

## Core Data Structures

```csharp
readonly record struct GeneratedTypeId(string SourcePath, string LogicalName, int Ordinal);

sealed class GeneratedTypePair
{
    public GeneratedTypeId Id { get; init; }
    public Type EvalType { get; set; }
    public TypeBuilder EvalTypeBuilder { get; set; }
    public Type PersistedType { get; set; }
    public TypeBuilder PersistedTypeBuilder { get; set; }
    public Dictionary<string, GeneratedMemberPair> Members { get; } = [];
}

sealed class GeneratedMemberPair
{
    public string LogicalName { get; init; }
    public MemberInfo EvalMember { get; set; }
    public MemberInfo PersistedMember { get; set; }
}
```

The exact implementation can be split by member kind, but every generated field, constructor, method, helper method, and delegate type needs the same idea: one logical artifact, two backend-local handles.

## Minimal Backend Surface

```csharp
interface ICompilationBackend
{
    bool CanRunNow { get; }
    TypeBuilder DefineType(GeneratedTypeId id, string runtimeName, Type baseType, TypeAttributes attrs);
    FieldBuilder DefineField(GeneratedTypeId owner, string logicalName, Type fieldType, FieldAttributes attrs);
    MethodBuilder DefineMethod(GeneratedTypeId owner, string logicalName, MethodAttributes attrs, Type returnType, Type[] args);
    ConstructorBuilder DefineConstructor(GeneratedTypeId owner, MethodAttributes attrs, CallingConventions conventions, Type[] args);
    CljILGen GetILGenerator(MethodBase method);
    Type FinalizeType(GeneratedTypeId id);
}
```

For the first implementation, this can wrap existing Reflection.Emit objects. It does not need to abstract every Cecil feature yet.

## Finalization Rules

- Eval type finalization may happen as soon as compile-time execution needs a real `Type`.
- Persisted type finalization should be delayed until all persisted references for the namespace are known, unless Reflection.Emit requires earlier finalization for signatures.
- If early finalization is unavoidable, all later references must use the finalized persisted `Type`, never the eval `Type`.
- Helper and delegate types created by `DynInitHelper` must be finalized inside the same backend universe as the method bodies that reference them.

## Constants, Vars, Keywords, Call-Sites

- Vars and Keywords are runtime library objects and can be emitted independently into both backends.
- Constant fields need paired field records when the constant is referenced from generated IL.
- Constants that are generated `Type` values need logical type ids, not raw `System.Type` instances from the other universe.
- Direct links should store `Var -> GeneratedTypeId` plus target method logical name.
- Dynamic call-sites should store logical ids for helper type, delegate type, call-site field, and setter method.

## First-Pass Policy

Required:

- `ns`
- simple `def`
- `defn`
- top-level function invocation
- naked top-level `let`
- macro definition/use if it only needs eval-side execution and persisted init replay

Constrain or disable:

- Direct linking is disabled for `net9.0+` persisted assembly compilation in the first pass, even when the compiler option requests it. It should only be re-enabled after `StaticInvokeExpr` can resolve through the persisted side of a generated-type pair and eval can resolve through the eval side.

Explicitly defer:

- `deftype*`
- `reify*`
- `gen-class`
- `proxy`
- async method flags
- debug symbols beyond current persisted save behavior
- cross-target reference assembly support
