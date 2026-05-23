# Goal: Restore AOT Compilation for ClojureCLR on Modern .NET

## Source

This goal is derived from `research/raw/AOT-compilation-issues.html`, a February 20, 2025 post describing why restoring ClojureCLR AOT compilation on .NET 9 is not a simple reactivation of the old .NET Framework implementation.

## Problem Statement

ClojureCLR used to support ahead-of-time compilation on .NET Framework by emitting a saveable dynamic assembly, progressively evaluating each source form as it was read, and finally saving the generated assembly as a DLL. That worked because .NET Framework's `AssemblyBuilder` could both run emitted types during the same process and save the resulting assembly to disk.

Modern .NET removed dynamic assembly saving for a long period. .NET 9 reintroduces saving through `System.Reflection.Emit.PersistedAssemblyBuilder`, but this is a separate persisted emit implementation. Types emitted into a persisted assembly cannot be executed until the assembly is saved and loaded back. This breaks ClojureCLR's compilation model because Clojure loading requires progressive environment updates: each top-level form must be evaluated before the next form is read so later forms can observe newly-created Vars, namespace mappings, function values, macros, and other runtime effects.

The core problem to solve:

> Restore ClojureCLR AOT compilation on .NET 9+ while preserving Clojure's progressive load/eval semantics.

## Why This Is Hard

- ClojureCLR compilation is not a pure "read all source, emit all code later" pipeline.
- Analysis and code generation are currently intertwined.
- Many forms create runtime types, including obvious function forms such as `fn`, `defn`, and `deftype`, plus less obvious compiler transformations such as naked `let` forms being wrapped in generated functions.
- During compilation, generated code must be runnable immediately so the Clojure environment can be updated before reading the next form.
- `PersistedAssemblyBuilder` can save emitted IL, but emitted persisted types cannot participate in that progressive evaluation.
- Cross-references between runnable dynamic assembly types and persisted assembly types are invalid:
  - If dynamic eval code references persisted types, the code cannot run before the persisted assembly is saved and loaded.
  - If persisted code references eval assembly types, the persisted assembly cannot be saved or later loaded as a self-contained compiled library.
- Persisted assembly generation may be target-framework-dependent. Correct use of `PersistedAssemblyBuilder` may require selecting the right core/reference assemblies for the intended TFM, possibly through `MetadataLoadContext`.

## Desired Outcome

Implement an AOT compilation path that:

1. Compiles a Clojure namespace into a loadable DLL on .NET 9+.
2. Preserves normal Clojure loading behavior while compiling.
3. Emits persisted code that is independent of temporary eval-only generated types.
4. Produces initialization code equivalent to source loading: creating Vars, installing namespace mappings, assigning root values, and running required top-level side effects.
5. Allows compiled Clojure runtime namespaces, especially `core.clj`, to be distributed or generated in a way that materially improves startup time.

## Initial clojure.main Benchmark Target

The first concrete target for measuring source-load startup should be:

`Clojure/Clojure.Samples/clojure/samples/spec_schema.clj`

Run it as a script through `clojure.main` from a fresh process, for example by passing that file path to `Clojure.Main.dll`. This target is intentionally a small namespace load that exercises `clojure.spec`: it establishes `clojure.samples.spec-schema`, requires `clojure.spec.alpha` and `clojure.spec.test.alpha`, defines predicate functions, specs, sample JSON-like maps, an instrumented `line-total` function, and computes `sample-results` at top level.

Timing expectations for this target:

- Measure only the time to start ClojureCLR and load the file through `clojure.main`.
- The target budget is sub-500 ms total wall-clock time for that fresh-process startup plus file load.
- Do not call any additional workload function as part of the startup benchmark.
- Include the normal top-level effects of loading the file, including the single `sample-results` computation, but do not add extra REPL evaluation or printing.
- If this file takes materially longer than 500 ms when used as the startup benchmark, treat that as a startup/load regression or benchmark harness problem, not as expected behavior from the sample itself.

## Leading Implementation Direction

The likely approach is dual code generation:

- Analyze each form once.
- Emit one runnable version of each generated type into the existing eval/dynamic assembly.
- Emit one persisted version of each generated type into a `PersistedAssemblyBuilder` assembly.
- Execute the eval version immediately to preserve progressive evaluation.
- Save the persisted assembly at the end of namespace compilation.
- When loading a compiled namespace later, load the DLL and call its generated initialization method.

This requires auditing and possibly restructuring compiler code so every generated type, method, field, constructor, constant, and reference is emitted consistently into the correct assembly universe.

## First Engineering Milestone

Audit the compiler's code generation paths and identify every place that can create or reference generated types during analysis or emit.

For each path, classify:

- Whether it creates a type during analysis.
- Whether it finalizes a type before full form evaluation.
- Whether it references another generated type.
- Whether the reference would point to eval-only code, persisted code, runtime library code, or user/library static code.
- Whether the same operation can be reproduced cleanly for both eval and persisted emission.

The immediate deliverable from this milestone should be a concrete list of compiler changes needed to prevent eval/persisted cross-contamination.

## Acceptance Criteria

A solution should be considered viable when the following work:

- `(compile 'some.ns)` emits a DLL for a namespace containing ordinary top-level `def`, `defn`, `let`, and function invocation forms.
- Later forms in the same namespace can use Vars and values established by earlier forms during compilation.
- Loading the emitted DLL initializes the namespace without reading the original `.clj` source.
- Persisted emitted types do not reference eval assembly types.
- Eval emitted types do not depend on persisted assembly types for compile-time execution.
- The generated DLL can be saved, loaded into a fresh process, and used through normal ClojureCLR namespace loading.
- Compiler behavior remains correct when macros or other top-level compile-time effects require immediate evaluation.
- There is a documented policy for target framework handling: same-runtime-only first, or explicit TFM/reference-assembly support if needed.

## Constraints

- Preserve Clojure semantics. Startup speed is valuable, but not at the cost of changing load/eval behavior.
- Do not assume the .NET Framework `AssemblyBuilder.Save` model still exists.
- Treat `PersistedAssemblyBuilder` and runnable dynamic emit as two separate emit backends.
- Avoid references from persisted output to transient eval-only generated artifacts.
- Keep the first working version narrow enough to prove the architecture before expanding to all compiler forms.

## Open Questions

- Can all compiler code generation be replayed reliably from a single analyzed AST, or do some analysis steps currently depend on side effects from already-generated CLR types?
- Which generated type references are currently implicit and need to become backend-aware?
- Can direct linking or other optimization paths be disabled initially to reduce the AOT restoration surface?
- Should compiled runtime libraries be shipped per target framework, generated during installation, or generated on first run?
- What is the minimal namespace that proves the dual-generation model before attempting `clojure.core`?

## Non-Goals For The First Pass

- Rewriting the compiler around Roslyn.
- Replacing Reflection.Emit with Mono.Cecil or ILPack before proving that `PersistedAssemblyBuilder` cannot support the required output.
- Designing a new intermediate persisted AST/cache format as the primary solution.
- Solving every target-framework packaging problem before validating the core eval-plus-persisted compilation model.

## Practical Next Steps

1. Locate the existing AOT and dynamic assembly creation code.
2. Locate all compiler paths that call type creation/finalization APIs during analysis.
3. Build a small experiment that emits equivalent simple function types into both a runnable dynamic assembly and a persisted assembly.
4. Verify that the runnable type can update the Clojure environment while the persisted type remains saveable and independent.
5. Extend the experiment to a namespace with multiple forms where later forms depend on earlier Vars.
6. Use `Clojure/Clojure.Samples/clojure/samples/spec_schema.clj` as the first `clojure.main` source-load benchmark target, measuring only startup plus file load against a sub-500 ms budget.
7. Convert the experiment into a compiler abstraction only after the cross-reference rules are understood.
