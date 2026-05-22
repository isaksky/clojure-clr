using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace clojure.lang.CljCompiler.Context;

public enum GeneratedArtifactBackend
{
    Unknown = 0,
    Eval = 1,
    Persisted = 2
}

public enum GeneratedMemberKind
{
    Constructor,
    Field,
    Method,
    StaticConstructor
}

public readonly record struct GeneratedTypeId(string SourcePath, string LogicalName, int Ordinal);

public readonly record struct GeneratedMemberId(
    GeneratedTypeId Owner,
    GeneratedMemberKind Kind,
    string LogicalName,
    int Ordinal);

public sealed class GeneratedTypeRecord
{
    private readonly Dictionary<GeneratedArtifactBackend, string> _runtimeNames = [];
    private readonly Dictionary<GeneratedMemberId, GeneratedMemberRecord> _members = [];
    private TypeBuilder _evalTypeBuilder;
    private TypeBuilder _persistedTypeBuilder;
    private TypeBuilder _unknownTypeBuilder;
    private Type _evalType;
    private Type _persistedType;
    private Type _unknownType;

    internal GeneratedTypeRecord(GeneratedTypeId id)
    {
        Id = id;
    }

    public GeneratedTypeId Id { get; }
    public TypeBuilder EvalTypeBuilder => _evalTypeBuilder;
    public TypeBuilder PersistedTypeBuilder => _persistedTypeBuilder;
    public TypeBuilder UnknownTypeBuilder => _unknownTypeBuilder;
    public Type EvalType => _evalType;
    public Type PersistedType => _persistedType;
    public Type UnknownType => _unknownType;
    public IReadOnlyDictionary<GeneratedMemberId, GeneratedMemberRecord> Members => _members;

    public string GetRuntimeName(GeneratedArtifactBackend backend)
    {
        return _runtimeNames.TryGetValue(backend, out string name) ? name : null;
    }

    public TypeBuilder GetTypeBuilder(GeneratedArtifactBackend backend)
    {
        return backend switch
        {
            GeneratedArtifactBackend.Eval => _evalTypeBuilder,
            GeneratedArtifactBackend.Persisted => _persistedTypeBuilder,
            _ => _unknownTypeBuilder
        };
    }

    public Type GetCreatedType(GeneratedArtifactBackend backend)
    {
        return backend switch
        {
            GeneratedArtifactBackend.Eval => _evalType,
            GeneratedArtifactBackend.Persisted => _persistedType,
            _ => _unknownType
        };
    }

    internal void SetTypeBuilder(GeneratedArtifactBackend backend, string runtimeName, TypeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SetRuntimeName(backend, runtimeName);

        switch (backend)
        {
            case GeneratedArtifactBackend.Eval:
                SetOnce(ref _evalTypeBuilder, builder, nameof(EvalTypeBuilder));
                break;
            case GeneratedArtifactBackend.Persisted:
                SetOnce(ref _persistedTypeBuilder, builder, nameof(PersistedTypeBuilder));
                break;
            default:
                SetOnce(ref _unknownTypeBuilder, builder, nameof(UnknownTypeBuilder));
                break;
        }
    }

    internal void SetCreatedType(GeneratedArtifactBackend backend, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        switch (backend)
        {
            case GeneratedArtifactBackend.Eval:
                SetOnce(ref _evalType, type, nameof(EvalType));
                break;
            case GeneratedArtifactBackend.Persisted:
                SetOnce(ref _persistedType, type, nameof(PersistedType));
                break;
            default:
                SetOnce(ref _unknownType, type, nameof(UnknownType));
                break;
        }
    }

    internal void AddMember(GeneratedMemberRecord member)
    {
        _members[member.Id] = member;
    }

    internal void SetRuntimeName(GeneratedArtifactBackend backend, string runtimeName)
    {
        if (string.IsNullOrEmpty(runtimeName))
            return;

        if (_runtimeNames.TryGetValue(backend, out string existing) && existing != runtimeName)
            throw new InvalidOperationException($"Generated type {Id} already has {backend} runtime name {existing}.");

        _runtimeNames[backend] = runtimeName;
    }

    private static void SetOnce<T>(ref T field, T value, string fieldName)
        where T : class
    {
        if (field is not null && !ReferenceEquals(field, value))
            throw new InvalidOperationException($"Generated artifact field {fieldName} was already assigned.");

        field = value;
    }
}

public sealed class GeneratedMemberRecord
{
    private MemberInfo _evalMember;
    private MemberInfo _persistedMember;
    private MemberInfo _unknownMember;

    internal GeneratedMemberRecord(GeneratedMemberId id)
    {
        Id = id;
    }

    public GeneratedMemberId Id { get; }
    public MemberInfo EvalMember => _evalMember;
    public MemberInfo PersistedMember => _persistedMember;
    public MemberInfo UnknownMember => _unknownMember;

    public MemberInfo GetMember(GeneratedArtifactBackend backend)
    {
        return backend switch
        {
            GeneratedArtifactBackend.Eval => _evalMember,
            GeneratedArtifactBackend.Persisted => _persistedMember,
            _ => _unknownMember
        };
    }

    internal void SetMember(GeneratedArtifactBackend backend, MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(member);

        switch (backend)
        {
            case GeneratedArtifactBackend.Eval:
                SetOnce(ref _evalMember, member, nameof(EvalMember));
                break;
            case GeneratedArtifactBackend.Persisted:
                SetOnce(ref _persistedMember, member, nameof(PersistedMember));
                break;
            default:
                SetOnce(ref _unknownMember, member, nameof(UnknownMember));
                break;
        }
    }

    private static void SetOnce<T>(ref T field, T value, string fieldName)
        where T : class
    {
        if (field is not null && !ReferenceEquals(field, value))
            throw new InvalidOperationException($"Generated artifact field {fieldName} was already assigned.");

        field = value;
    }
}

public sealed class GeneratedArtifactRegistry
{
    private readonly Dictionary<GeneratedTypeId, GeneratedTypeRecord> _types = [];
    private readonly Dictionary<GeneratedMemberId, GeneratedMemberRecord> _members = [];
    private readonly Dictionary<GeneratedArtifactBackend, Dictionary<TypeCounterKey, int>> _typeOrdinals = [];
    private readonly Dictionary<GeneratedArtifactBackend, Dictionary<MemberCounterKey, int>> _memberOrdinals = [];

    public IReadOnlyCollection<GeneratedTypeRecord> Types => _types.Values;
    public IReadOnlyCollection<GeneratedMemberRecord> Members => _members.Values;

    public GeneratedTypeRecord DeclareType(
        GeneratedArtifactBackend backend,
        string sourcePath,
        string logicalName,
        string runtimeName)
    {
        sourcePath ??= string.Empty;
        logicalName = NormalizeLogicalName(logicalName);

        int ordinal = NextOrdinal(_typeOrdinals, backend, new TypeCounterKey(sourcePath, logicalName));
        GeneratedTypeId id = new(sourcePath, logicalName, ordinal);

        if (!_types.TryGetValue(id, out GeneratedTypeRecord record))
        {
            record = new GeneratedTypeRecord(id);
            _types[id] = record;
        }

        record.SetRuntimeName(backend, runtimeName);

        return record;
    }

    public GeneratedTypeRecord RegisterTypeBuilder(
        GeneratedArtifactBackend backend,
        string sourcePath,
        string logicalName,
        string runtimeName,
        TypeBuilder builder)
    {
        GeneratedTypeRecord record = DeclareType(backend, sourcePath, logicalName, runtimeName);
        record.SetTypeBuilder(backend, runtimeName, builder);
        return record;
    }

    public void RegisterCreatedType(GeneratedArtifactBackend backend, GeneratedTypeId id, Type type)
    {
        if (!_types.TryGetValue(id, out GeneratedTypeRecord record))
            throw new InvalidOperationException($"Generated type id {id} has not been declared.");

        record.SetCreatedType(backend, type);
    }

    public GeneratedMemberRecord DeclareMember(
        GeneratedArtifactBackend backend,
        GeneratedTypeId owner,
        GeneratedMemberKind kind,
        string logicalName)
    {
        if (!_types.ContainsKey(owner))
            throw new InvalidOperationException($"Generated owner type id {owner} has not been declared.");

        logicalName = NormalizeLogicalName(logicalName);

        int ordinal = NextOrdinal(_memberOrdinals, backend, new MemberCounterKey(owner, kind, logicalName));
        GeneratedMemberId id = new(owner, kind, logicalName, ordinal);

        if (!_members.TryGetValue(id, out GeneratedMemberRecord record))
        {
            record = new GeneratedMemberRecord(id);
            _members[id] = record;
            _types[owner].AddMember(record);
        }

        return record;
    }

    public GeneratedMemberRecord RegisterMember(
        GeneratedArtifactBackend backend,
        GeneratedTypeId owner,
        GeneratedMemberKind kind,
        string logicalName,
        MemberInfo member)
    {
        GeneratedMemberRecord record = DeclareMember(backend, owner, kind, logicalName);
        record.SetMember(backend, member);
        return record;
    }

    public bool TryGetType(GeneratedTypeId id, out GeneratedTypeRecord record)
    {
        return _types.TryGetValue(id, out record);
    }

    public bool TryGetMember(GeneratedMemberId id, out GeneratedMemberRecord record)
    {
        return _members.TryGetValue(id, out record);
    }

    private static string NormalizeLogicalName(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
            throw new ArgumentException("Generated artifact logical name cannot be null or empty.", nameof(logicalName));

        return logicalName;
    }

    private static int NextOrdinal<TKey>(
        Dictionary<GeneratedArtifactBackend, Dictionary<TKey, int>> ordinals,
        GeneratedArtifactBackend backend,
        TKey key)
        where TKey : notnull
    {
        if (!ordinals.TryGetValue(backend, out Dictionary<TKey, int> backendOrdinals))
        {
            backendOrdinals = [];
            ordinals[backend] = backendOrdinals;
        }

        backendOrdinals.TryGetValue(key, out int ordinal);
        backendOrdinals[key] = ordinal + 1;
        return ordinal;
    }

    private readonly record struct TypeCounterKey(string SourcePath, string LogicalName);

    private readonly record struct MemberCounterKey(
        GeneratedTypeId Owner,
        GeneratedMemberKind Kind,
        string LogicalName);
}
