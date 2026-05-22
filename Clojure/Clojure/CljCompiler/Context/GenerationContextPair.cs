using System;

namespace clojure.lang.CljCompiler.Context;

public sealed class GenerationContextPair
{
    private GenerationContextPair(GenContext evalContext, GenContext persistedContext)
    {
        EvalContext = evalContext ?? throw new ArgumentNullException(nameof(evalContext));
        PersistedContext = persistedContext;
        GeneratedArtifacts = evalContext.GeneratedArtifacts;

        if (persistedContext is not null && !ReferenceEquals(GeneratedArtifacts, persistedContext.GeneratedArtifacts))
            throw new ArgumentException("Generation contexts must share one generated artifact registry.", nameof(persistedContext));
    }

    public GenContext EvalContext { get; }
    public GenContext PersistedContext { get; }
    public GeneratedArtifactRegistry GeneratedArtifacts { get; }
    public bool HasPersistedContext => PersistedContext is not null;

    internal static GenerationContextPair CreateEvalOnly(GenContext evalContext)
    {
        if (evalContext.ArtifactBackend != GeneratedArtifactBackend.Eval)
            throw new ArgumentException("Eval-only generation context must use the eval backend.", nameof(evalContext));

        GenerationContextPair contexts = new(evalContext, null);
        evalContext.AttachGenerationContexts(contexts);
        return contexts;
    }

    internal static GenerationContextPair CreatePaired(GenContext evalContext, GenContext persistedContext)
    {
        if (evalContext.ArtifactBackend != GeneratedArtifactBackend.Eval)
            throw new ArgumentException("Paired eval context must use the eval backend.", nameof(evalContext));
        if (persistedContext.ArtifactBackend != GeneratedArtifactBackend.Persisted)
            throw new ArgumentException("Paired persisted context must use the persisted backend.", nameof(persistedContext));

        GenerationContextPair contexts = new(evalContext, persistedContext);
        evalContext.AttachGenerationContexts(contexts);
        persistedContext.AttachGenerationContexts(contexts);
        return contexts;
    }

    public GenContext ForBackend(GeneratedArtifactBackend backend)
    {
        return backend switch
        {
            GeneratedArtifactBackend.Eval => EvalContext,
            GeneratedArtifactBackend.Persisted => PersistedContext,
            _ => null
        };
    }
}
