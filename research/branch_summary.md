# Branch Summary

This branch is focused on restoring useful modern .NET AOT for ClojureCLR and
then proving that the restored path materially improves `Clojure.Main` startup.
It started from the research goal in [`Goal.md`](Goal.md): preserve progressive
Clojure load/eval semantics while producing source-free persisted namespace
DLLs on .NET 9+.

## Direct `Clojure.Main` Command-Line Startup

Same-session measurements were taken on 2026-05-26 MDT on macOS 15.7.3 arm64
with .NET SDK `10.0.107` and runtime `10.0.7`.

- Branch under test: `isak-aot` at `fd5af306`, plus local documentation edits.
- Configuration: Release `net10.0`.
- Command shape: `dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll ...`.
- Timing harness: 1 warmup and 3 measured fresh processes.
- Setup: built `Clojure.Main`, built `Clojure.Compile`, then prepared the 48
  generated `clojure.*.clj/c.dll` namespace assemblies in the default
  `Clojure.Main` output as ReadyToRun images with
  `research/scripts/readytorun-generated-clj-dlls.zsh`.
- Gate result: `research/scripts/check-clojure-main-startup-suite.zsh` passed
  with `RUNS=3 WARMUPS=1 MAX_MS=500`; the slowest measured run was `284.8 ms`.

| Startup probe | Status | Median / p95 |
| --- | --- | ---: |
| Baseline expression: `-e "(println :ok)"` | Pass | `175.7 ms` / `179.4 ms` |
| Macro definition and expansion | Pass | `215.6 ms` / `215.8 ms` |
| Destructuring expression | Pass | `249.7 ms` / `254.1 ms` |
| Protocol plus `deftype` | Pass | `235.7 ms` / `237.0 ms` |
| Multimethod dispatch | Pass | `214.4 ms` / `214.7 ms` |
| Lazy seq realization | Pass | `180.3 ms` / `182.3 ms` |
| First `clojure.string` require expression | Pass | `192.4 ms` / `199.6 ms` |
| `clojure.spec.alpha` validation | Pass | `212.5 ms` / `213.8 ms` |
| `clojure.spec.test.alpha` instrumentation | Pass | `211.7 ms` / `213.4 ms` |
| Generated feature script from the startup gate | Pass | `243.2 ms` / `244.7 ms` |
| `Clojure/Clojure.Samples/clojure/samples/stm/teststm.clj` | Pass | `211.8 ms` / `227.0 ms` |
| `Clojure/Clojure.Samples/clojure/samples/spec_schema.clj` | Pass | `248.5 ms` / `252.2 ms` |
| `Clojure/Clojure.Samples/clojure/samples/newtonsoft_demo.cljr` | Pass | `277.7 ms` / `284.8 ms` |
| `Clojure/Clojure.Samples/clojure/samples/sqlite_demo.cljr` | Pass | `249.9 ms` / `257.2 ms` |

Historical master comparisons and older 10-run measurements are kept in the
focused result files under [`research/results`](results). The current direct
`clojure.main` command-line check confirms that this branch's compiled/R2R
namespace path is comfortably below the sub-500 ms startup budget for the tested
scripts.

## Research And Direction

| Work item | What we tried or implemented | Status |
| --- | --- | --- |
| AOT problem statement | Captured why the old .NET Framework `AssemblyBuilder.Save` model does not map directly to modern `PersistedAssemblyBuilder`: persisted types cannot run until saved and loaded, but Clojure loading needs progressive evaluation. | Complete; documented in [`Goal.md`](Goal.md). |
| Raw source material | Archived the February 2025 AOT compilation discussion in `research/raw/AOT-compilation-issues.html`. | Complete. |
| Current AOT baseline | Built and inspected a minimal namespace containing `def`, `defn`, top-level invocation, and top-level `let`; proved source-free load and IL verification for the initial saved DLL. | Complete; documented in [`results/current-aot-behavior.md`](results/current-aot-behavior.md). |
| Dual-generation prototype | Wrote a minimal C# experiment with one runnable dynamic type and one persisted type that both update the same Var-like state, including fresh-process load and IL verification. | Complete; validated the paired Reflection.Emit approach. |
| Backend survey | Reviewed Cecil/XamlX/Avalonia/Fody/ILRepack/coverlet patterns for future backend abstraction, resolver behavior, symbol writing, and metadata rewriting. | Complete as research; Cecil backend is deferred. |
| Backend decision | Chose staged Reflection.Emit plus `PersistedAssemblyBuilder` before attempting Mono.Cecil. | Kept; current implementation confirms the decision for the verified slices. |
| Generated type inventory | Audited generated type creation and reference paths across function classes, generated forms, helpers, constants, fields, methods, constructors, and namespace initializers. | Complete enough for current implementation; continue expanding if new compiler forms are added. |
| Progressive semantics map | Mapped where later forms depend on earlier compile-time effects, especially macro definitions and top-level Vars. | Complete for current tests; covered by progressive macro AOT tests. |
| Cross-reference risk map | Identified eval/persisted contamination risks and the places where raw `Type` or member handles could leak across assembly universes. | Complete for implemented slices; still the right checklist for new forms. |

## Persisted AOT Implementation

| Work item | What we tried or implemented | Status |
| --- | --- | --- |
| Modern compile driver | Changed `net9.0+` compile post-build execution from `mono $(TargetPath)` to `dotnet "$(TargetPath)"`. | Implemented and kept. |
| `Clojure.Main` AOT copy paths | Replaced solution-relative copy assumptions with project-relative paths so direct project builds copy generated namespace DLLs into the main output. | Implemented and kept. |
| Explicit generation context pair | Added an explicit pair for persisted and eval `GenContext` instances, with separate assembly/type universes. | Implemented. |
| Generated artifact registry | Added backend-aware records for generated types and members so persisted and eval handles share a logical identity without sharing raw handles. | Implemented and unit-tested. |
| Function class identity pairing | Routed generated function classes through the registry, including backend-local names and member records. | Implemented. |
| Namespace initializer replay | Persisted namespace DLLs contain initializer types/methods that recreate namespace state without reading source. | Implemented; covered by source-free load tests. |
| Direct linking | Initially disabled for persisted modern AOT to avoid `Var -> Type` records pointing at the wrong assembly universe. | Implemented as a guard; backend-aware direct linking is deferred. |
| Constants, Vars, Keywords, members | Recorded generated constants, Vars, Keywords, constructors, fields, methods, and helper members through backend-local artifacts. | Implemented for current AOT paths. |
| Progressive macro behavior | Verified that a macro defined by an earlier form can affect later forms during compilation and that the persisted result loads source-free. | Implemented and tested. |
| Dynamic host interop | Initially treated as too risky, then implemented persisted/eval DLR helper generation through the active backend context. | Implemented and tested for same-runtime and explicit-target cases. |
| `gen-interface` | Paired generated interfaces and methods across persisted/eval contexts. | Implemented. |
| `gen-delegate` | Kept delegate wrapper classes runtime-only; persisted code calls `GenDelegate.Create` rather than saving transient wrapper types. | Implemented by policy and tested. |
| `deftype` and `reify` | Paired abstract/stub types, implementation types, fields, constructors, override metadata, and protocol/interface methods. | Implemented and tested. |
| `gen-class` | Kept standalone class assembly lifecycle, saving the persisted class assembly and loading it back for compile-time visibility. | Implemented. |
| `proxy` | Emits persisted proxy classes into the namespace DLL while the eval pass creates runnable proxy types for progressive execution. | Implemented and tested. |
| `gen-class :main true` | Updated the persisted AOT tests to assert restored main-method and entry-point support instead of expecting rejection. | Implemented; beads `clojure-clr-b24` and `clojure-clr-3yl` are closed. |
| Runtime namespace tranche | Verified persisted AOT for `clojure.walk`, `clojure.template`, `clojure.set`, `clojure.string`, and `clojure.data`, including source-free fresh-process behavior. | Implemented and tested. |
| Standard namespace compilation | Release `net10.0` build now compiles the standard runtime namespaces plus spec namespaces into `.clj.dll` files. | Implemented. |
| Explicit target framework selection | Added reference assembly selection through `MetadataLoadContext`, with same-runtime default policy. | Implemented for verified cases. |
| Target framework metadata | Saved assemblies are stamped with target framework metadata and avoid runtime implementation assembly leaks in explicit-target cases. | Implemented and tested. |
| Custom attributes and metadata | Added target-aware metadata/custom attribute emission, including named members and Type-valued metadata constants. | Implemented and tested. |
| Portable debug symbols | Added manual PE/PDB save path for verified Debug portable PDB output when the debug writer is active. | Implemented; Debug/ILVerify gates remain opt-in. |
| Cecil backend replacement | Considered as an alternative implementation route. | Deferred; current Reflection.Emit/PersistedAssemblyBuilder path is viable for the current milestone. |

## Startup And Packaging Work

| Work item | What we tried or implemented | Status |
| --- | --- | --- |
| Default startup diagnosis | Found that default `Clojure.Main` output source-loaded because compiled runtime DLLs were not copied into the main output. | Fixed. |
| Initial startup improvement | After copy-path fixes, Release `net10.0` trivial startup improved from roughly `5.2-5.4 s` source-loading behavior to about `1.0 s`. | Kept, then improved further. |
| `clojure.core.server` loading | Avoided eager server startup machinery unless `clojure.server.*` configuration exists. | Implemented and kept. |
| Eager `clojure.spec` in `clojure.main` | Removed eager spec loading from successful startup and lazily resolved spec explain formatting only on relevant error paths. | Implemented and kept. |
| Core macro spec fast path | Added conservative fast paths for common valid `clojure.core` macro forms before the spec stack is loaded; invalid or unsupported forms still fall back to the existing spec checker. The current implementation is about 119 nonblank/noncomment helper lines in `Compiler.cs`, plus a 4-line call site in `CheckSpecs` (about 128 code lines if the adjacent known-no-spec macro helper is counted too). After ReadyToRun this is no longer the primary startup win: a temporary disable test changed baseline `println` from `226.4 ms` to `222.3 ms`, `teststm.clj` from `268.8 ms` to `294.3 ms`, the generated feature script from `307.7 ms` to `307.5 ms`, and `spec_schema.clj` from `319.4 ms` to `332.7 ms`. It still saves about 13-26 ms on the macro-heavy measured script paths and remains useful for IL-only/non-R2R paths. | Implemented and tested; useful but not essential for the current R2R startup gate. |
| Macro spec environment names | Added portable uppercase env names alongside dotted JVM-style names for spec macro checks/asserts. | Implemented. |
| `teststm.clj` benchmark | Used as an earlier file-load startup target; confirmed the cost was first macro/spec initialization, not STM work. | Historical target; now passes under budget. |
| `spec_schema.clj` benchmark | Added the active spec-heavy sample target and measured source-load startup through `clojure.main`. | Implemented; passes under budget with generated namespace ReadyToRun preparation. |
| Spec namespace AOT | Compiled `clojure.spec.alpha`, `clojure.spec.gen.alpha`, `clojure.core.specs.alpha`, and branch-added `clojure.spec.test.alpha` into default output. | Implemented. |
| `clojure.spec.test.alpha` | Added source support for instrumentation/checking behavior with lazy generator loading on the normal instrumentation path. | Implemented and tested for startup path. |
| Direct initializer delegates | Replaced reflection `InvokeMember` initializer calls on generated namespace DLLs with direct initializer delegates. | Implemented; contributed to spec benchmark improvement. |
| ReadyToRun generated namespaces | Added `readytorun-generated-clj-dlls.zsh` to prepare generated `clojure.*.clj.dll` assemblies with crossgen2. | Implemented; required for the sub-500 ms script suite. |
| Startup gate script | Added `check-clojure-main-startup-suite.zsh` covering baseline expressions, macro expansion, destructuring, protocol/deftype, multimethods, lazy seqs, first require, spec validation/instrumentation, a generated feature script, `spec_schema`, and external package scripts. | Implemented; default budget is `MAX_MS=500`. |
| External package demos | Added `newtonsoft_demo.cljr` and `sqlite_demo.cljr` plus package output dependencies/assets for startup probes. | Implemented; branch passes the direct command-line startup gate. |
| Sudoku sample | Added Sudoku solver and Project Euler puzzle samples. | Added as sample code; not part of the current startup gate. |

## Startup Attempt Log

| Attempt | Result | Status |
| --- | --- | --- |
| Default source-loading master-like output | Trivial startup was around `5.2-5.4 s` before generated runtime DLLs were available in `Clojure.Main` output. | Rejected as too slow. |
| Manually supplied compiled runtime DLLs through `CLOJURE_LOAD_PATH` | Trivial startup improved to about `2.9 s`. | Useful diagnosis, not kept as supported packaging. |
| Project-relative copy-path fix | Release trivial startup reached about `1.0 s`. | Kept but insufficient. |
| Lazy `clojure.core.server` startup | Small improvement only; still far above budget. | Kept. |
| Experimental spec namespace compilation only | `spec_schema` improved from about `1.28 s` to about `768 ms`, but still missed the budget. | Kept as part of broader solution, insufficient alone. |
| Lazy spec loading in `clojure.main` | Trivial startup reached about `419 ms` median in the documented 10-run run. | Kept. |
| Core macro spec fast path | `teststm.clj` improved from about `1016 ms` median to about `476 ms` median before generated namespace ReadyToRun became the supported startup path. Rechecking by temporarily disabling the fast path after R2R showed a smaller current benefit: `teststm.clj` worsened by about `25.5 ms` and `spec_schema.clj` by about `13.3 ms`, while baseline `println` and the generated feature script were effectively unchanged. | Kept, but classified as a secondary optimization rather than the main current startup win. |
| Spec path polish and direct initializer delegates | IL-only `spec_schema` path reached about `619 ms` median. | Kept, still insufficient alone. |
| ReadyToRun generated namespace DLLs | `spec_schema` reached about `306.9 ms` median in the documented 10-run run, and the current direct command-line check measured `248.5 ms` median / `252.2 ms` p95. | Kept; required packaging step for the startup gate. |
| External package startup probes | The current direct command-line check measured `newtonsoft_demo.cljr` at `277.7 ms` median / `284.8 ms` p95 and `sqlite_demo.cljr` at `249.9 ms` median / `257.2 ms` p95. | Kept. |

## Tests And Tooling

| Work item | What we tried or implemented | Status |
| --- | --- | --- |
| AOT regression fixture | Added broad `net9.0+` regression coverage for minimal persisted namespaces, source-free load, no eval/internal references, generated artifact records, dynamic interop, gen-interface/delegate/class/proxy, deftype/reify, explicit targets, metadata, debug symbols, runtime namespace tranche, spec startup, and ILVerify when configured. | Implemented. |
| Generated artifact registry tests | Added unit tests for pairing independent backend ordinals and member handles. | Implemented. |
| Startup measurement scripts | Added reusable scripts for generic startup, `teststm`, `spec_schema`, ReadyToRun preparation, and the full startup surprise gate. | Implemented. |
| Beads workflow | Added `.beads/`, `AGENTS.md`, and `scripts/codex_beads_loop.bb` for branch-local issue tracking. | Implemented. |
| `gen-class` main coverage | Full Release `net10.0` solution testing previously exposed stale rejection expectations; the current tests assert restored main-method and entry-point support. | Resolved. |

## Current State

The branch has a viable persisted AOT architecture for the current milestone:
compiled namespaces can be emitted, loaded without source, and kept separate
from eval-only dynamic artifacts for the tested compiler forms. The startup
work also changed the user-facing behavior: with compiled and ReadyToRun
generated namespace DLLs in the default `Clojure.Main` output, the current
direct command-line probes start in roughly `176-285 ms` on this machine.

There are no open ready beads at the time of this summary update.
