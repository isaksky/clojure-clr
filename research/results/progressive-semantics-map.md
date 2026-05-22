# Progressive Load/Eval Semantics Map

## Current Ordering

`Compiler.Compile` creates a persisted external `GenContext`, defines the namespace init type, and loops through source forms. For each form:

1. `Compile1` records source location metadata.
2. The form is macroexpanded.
3. `do` forms are recursively split so each child form gets its own compile/eval step.
4. The expanded form is analyzed with `RHC.Eval`.
5. The analyzed expression is emitted into the persisted namespace `Initialize` method.
6. If the expression is `(def ... (fn* ...))`, `RegisterDirectLink` records the generated persisted function type.
7. On `NET9_0_OR_GREATER`, `DoSeparateEval` re-evaluates the source form with compile bindings reset and `CompilerContextVar = null`.
8. The next form is read only after that eval completes.

At EOF, the compiler emits `ret`, constants initialization, the init type static constructor, a namespace description attribute, creates the init type, and saves the assembly.

## Form Buckets

| Bucket | Forms | What must happen before next read | Replay implication |
| --- | --- | --- | --- |
| Pure initializer emission plus separate eval | `ns`, `in-ns`, simple `def`, imports, most top-level calls with runtime-library targets | Persisted init receives equivalent IL; separate eval updates namespace/Vars immediately | Safe to emit persisted and eval separately if constants/Vars are independent |
| Analysis creates generated runtime types | `fn*`, `defn`, top-level anonymous functions, macro function values, load thunks | Current analysis finalizes a generated type via `FnExpr.Parse -> ObjExpr.Compile` | Needs paired type/member identity or two independent analyses |
| Emit finalizes generated types before eval | `fn*`, `defn`, `deftype*`, `reify*`; dynamic call-site helper types | Current generated `Type`/`MethodInfo`/`FieldInfo` objects are available before `DoSeparateEval` | Persisted references must not be reused by eval; eval references must not be saved |
| Eval requires generated code from just-analyzed form | Function-valued defs, macro defs, forms that call a just-defined fn during compilation | On .NET 9+, this is satisfied by re-evaluating the source form into the eval context | Persisted output cannot be the only generated code |
| Later macroexpansion depends on side effects | `(defmacro ...)` followed by macro use, `ns` alias/refer changes, `in-ns`, `load`, top-level Var mutation | Eval side effects must complete before the reader/macroexpander sees the later form | Any delayed batch backend must still preserve per-form eval |

## Representative Forms

| Form | Current persisted compile behavior | Current eval behavior | Risk |
| --- | --- | --- | --- |
| `(ns sample.ns)` | Emits init code that recreates namespace setup | `DoSeparateEval` changes `*ns*` and mappings before next form | Low |
| `(def answer 41)` | Emits Var intern/root binding in init | Eval interns/binds Var immediately | Low |
| `(defn inc-answer [] ...)` | `FnExpr.Parse` creates persisted `sample.ns$inc_answer`; init binds Var to new instance | Separate eval creates eval-only fn class and binds Var to eval instance | Medium: direct-link map stores persisted type |
| `(def invoked (inc-answer))` | May emit direct call if direct linking enabled and map is populated | Eval calls eval Var/function after previous form | Medium: persisted code should call persisted type; eval should call eval type |
| Naked top-level `(let [...])` | Emits expression into init; observed sample generated anonymous fn classes | Eval executes expression immediately for side effects/result discard | Medium: wrappers need paired identity if referenced |
| `defmacro` then macro use | Macro fn type emitted, Var metadata/root set | Eval must install macro before later macroexpand | High if eval cannot run generated macro |
| `deftype*`/`reify*` | Generates base and implementation types during analysis | Eval must produce runtime type usable by following forms | High: generated base/main type cross-links |
| `gen-class`, `proxy`, `gen-interface`, `gen-delegate` | Separate generators define and sometimes save types | Runtime/eval may observe generated `Type` or delegate immediately | High; defer from first milestone |

## Replay Boundaries

Allowed to replay into a second backend:

- AST emission for normal expressions after analysis, if all type/member references are backend-mapped.
- Constant, Var, Keyword, keyword call-site, and protocol call-site field definitions, if field identities are mapped.
- Function method bodies, if constructor/method/field references use backend-local equivalents.

Not safe to replay naively:

- Any AST that already captured a `Type`, `FieldInfo`, `MethodInfo`, or `ConstructorInfo` from a generated type in the other universe.
- Direct-link decisions represented as a plain `Type`.
- Duplicate-type lookup where a source name resolves to only one generated `Type`.
- Dynamic call-site delegates and `CallSite<T>` constructed types when `T` is backend-generated.
