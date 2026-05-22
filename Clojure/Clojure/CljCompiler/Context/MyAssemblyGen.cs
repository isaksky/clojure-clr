using Microsoft.Scripting.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;


#if ! NETFRAMEWORK
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using System.Collections.Immutable;
#endif

#if NET9_0_OR_GREATER
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
#endif

// This class is based on the original Microsoft code for Microsoft.Scripting.Gneeration.AssemblyGen.
// Lots of code copied here.
//
// // Licensed to the .NET Foundation under one or more agreements.
// // The .NET Foundation licenses this file to you under the Apache 2.0 License.
// // See the LICENSE file in the project root for more information.
// 

// Even before rolling my own version here, I had made my own versions of some its methods, such as MakeDelegateType.  
// The main adaptation here is to allow for creating a PersistedAssemblyBuilder for when we are in creating a persisted assembly in the context of .NET 9 or later.
// To help make the code clearer to the outside and easier to implement,  I created two constructors, one for persisted assemblies and one for non-persisted assemblies.
// The constructor for persisted assemblies is only visible in .NET Framework and .NET 9 or later.

namespace clojure.lang.CljCompiler.Context;

public sealed class MyAssemblyGen
{
    private readonly AssemblyBuilder _myAssembly;
    private readonly ModuleBuilder _myModule;
    private readonly bool _isDebuggable;
    private readonly bool _isPersistable;

    private int _index;


#if NETFRAMEWORK || NET9_0_OR_GREATER
    private readonly string _outFileName;       // can be null iff not saveable
    private readonly string _outDir;            // null means the current directory
#endif

#if NET9_0_OR_GREATER
    MethodBuilder _entryPointMethodBuilder;     // non-null means we have an entry point
    ISymbolDocumentWriter _docWriter = null;    // non-null means we are writing debug info
    private readonly Assembly _persistedCoreAssembly;
    private readonly MetadataLoadContext _persistedMetadataLoadContext;
    private readonly string _persistedTargetFramework;
    private readonly string _persistedReferenceAssemblyDirectory;
    private readonly Dictionary<Type, Type> _persistedTypeReferences = new();
    private readonly Dictionary<MemberInfo, MemberInfo> _persistedMemberReferences = new();
    private readonly Dictionary<string, Assembly> _persistedReferenceAssemblies = new(StringComparer.OrdinalIgnoreCase);
    public void SetDocWriter(ISymbolDocumentWriter dw) => _docWriter = dw;
    internal Assembly PersistedCoreAssembly => _persistedCoreAssembly;
    internal string PersistedTargetFramework => _persistedTargetFramework;
    internal string PersistedReferenceAssemblyDirectory => _persistedReferenceAssemblyDirectory;
#endif

    internal AssemblyBuilder AssemblyBuilder => _myAssembly;
    internal ModuleBuilder ModuleBuilder => _myModule;
    internal bool IsDebuggable => _isDebuggable;
    internal bool IsPersistable => _isPersistable;
    internal bool CanRunNow
    {
        get
        {
#if NET9_0_OR_GREATER
            return !_isPersistable;
#else
            return true;
#endif
        }
    }

    // This is the constructor for the non-persisted assembly.
    public MyAssemblyGen(AssemblyName name, bool isDebuggable)
    {
        ContractUtils.RequiresNotNull(name, nameof(name));

        _myAssembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        _myModule = _myAssembly.DefineDynamicModule(name.Name, isDebuggable);
        _isDebuggable = isDebuggable;
        _isPersistable = false;

#if NETFRAMEWORK || NET9_0_OR_GREATER
        _outFileName = null;
        _outDir = null;
#endif
#if NET9_0_OR_GREATER
        _persistedCoreAssembly = null;
        _persistedMetadataLoadContext = null;
        _persistedTargetFramework = null;
        _persistedReferenceAssemblyDirectory = null;
#endif

        if (isDebuggable)
        {
            SetDebuggableAttributes();
        }
    }

#if NETFRAMEWORK || NET9_0_OR_GREATER

    // This is the constructor for the persisted assembly.
    public MyAssemblyGen(AssemblyName name, string outDir, string outFileExtension, bool isDebuggable, IDictionary<string, object> attrs = null)
    {
        ContractUtils.RequiresNotNull(name, nameof(name));

        if (outFileExtension == null)
        {
            outFileExtension = ".dll";
        }

        if (outDir != null)
        {
            try
            {
                outDir = Path.GetFullPath(outDir);
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Invalid directory name");
            }
            try
            {
                Path.Combine(outDir, name.Name + outFileExtension);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("Invalid assembly name or extension");
            }

            _outFileName = name.Name + outFileExtension;
            _outDir = outDir;
        }


        var attributes = new List<CustomAttributeBuilder>();

#if NETFRAMEWORK
        // mark the assembly transparent so that it works in partial trust:
        attributes.Add(new CustomAttributeBuilder(typeof(SecurityTransparentAttribute).GetConstructor(ReflectionUtils.EmptyTypes), ArrayUtils.EmptyObjects));

        if (attrs != null)
        {
            foreach (var attr in attrs)
            {
                if (!(attr.Value is string a) || string.IsNullOrWhiteSpace(a))
                {
                    continue;
                }

                ConstructorInfo ctor = null;
                switch (attr.Key)
                {
                    case "assemblyFileVersion":
                        ctor = typeof(AssemblyFileVersionAttribute).GetConstructor(new[] { typeof(string) });
                        break;
                    case "copyright":
                        ctor = typeof(AssemblyCopyrightAttribute).GetConstructor(new[] { typeof(string) });
                        break;
                    case "productName":
                        ctor = typeof(AssemblyProductAttribute).GetConstructor(new[] { typeof(string) });
                        break;
                    case "productVersion":
                        ctor = typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) });
                        break;
                }

                if (ctor != null)
                {
                    attributes.Add(new CustomAttributeBuilder(ctor, new object[] { a }));
                }
            }
        }
#endif

#if NETFRAMEWORK
        _myAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndSave, outDir, false, attributes);
        _myModule = _myAssembly.DefineDynamicModule(name.Name, _outFileName, isDebuggable);
        _myAssembly.DefineVersionInfoResource();
#elif NET9_0_OR_GREATER
        PersistedCoreAssemblySelection coreSelection = SelectPersistedCoreAssembly();
        _persistedCoreAssembly = coreSelection.CoreAssembly;
        _persistedMetadataLoadContext = coreSelection.MetadataLoadContext;
        _persistedTargetFramework = coreSelection.TargetFramework;
        _persistedReferenceAssemblyDirectory = coreSelection.ReferenceAssemblyDirectory;
        PersistedAssemblyBuilder ab = new PersistedAssemblyBuilder(name, _persistedCoreAssembly, attributes);
        _myAssembly = ab;
        _myModule = ab.DefineDynamicModule(name.Name, isDebuggable);
#endif
        _isPersistable = true;
        _isDebuggable = isDebuggable;

        if (isDebuggable) {
            SetDebuggableAttributes();
        }


    }
#endif

    internal void SetDebuggableAttributes()
    {
#if NET9_0_OR_GREATER
        if (_persistedMetadataLoadContext is not null)
        {
            // CustomAttributeBuilder does not accept MetadataLoadContext constructors.
            return;
        }
#endif

        DebuggableAttribute.DebuggingModes attrs =
            DebuggableAttribute.DebuggingModes.Default |
            DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints |
            DebuggableAttribute.DebuggingModes.DisableOptimizations;

        Type[] argTypes = new Type[] { typeof(DebuggableAttribute.DebuggingModes) };
        Object[] argValues = new Object[] { attrs };

        var debuggableCtor = typeof(DebuggableAttribute).GetConstructor(argTypes);

        _myAssembly.SetCustomAttribute(new CustomAttributeBuilder(debuggableCtor, argValues));
        _myModule.SetCustomAttribute(new CustomAttributeBuilder(debuggableCtor, argValues));
    }

#if NET9_0_OR_GREATER
    private sealed class PersistedCoreAssemblySelection
    {
        internal PersistedCoreAssemblySelection(
            Assembly coreAssembly,
            MetadataLoadContext metadataLoadContext,
            string targetFramework,
            string referenceAssemblyDirectory)
        {
            CoreAssembly = coreAssembly;
            MetadataLoadContext = metadataLoadContext;
            TargetFramework = targetFramework;
            ReferenceAssemblyDirectory = referenceAssemblyDirectory;
        }

        internal Assembly CoreAssembly { get; }
        internal MetadataLoadContext MetadataLoadContext { get; }
        internal string TargetFramework { get; }
        internal string ReferenceAssemblyDirectory { get; }
    }

    private static PersistedCoreAssemblySelection SelectPersistedCoreAssembly()
    {
        string targetFramework = NormalizeTargetFrameworkOption(Compiler.PersistedAotTargetFramework());
        string referenceAssemblyDirectory = Compiler.PersistedAotReferenceAssemblyPath();

        if (targetFramework is null && string.IsNullOrWhiteSpace(referenceAssemblyDirectory))
            return new PersistedCoreAssemblySelection(typeof(object).Assembly, null, null, null);

        string resolvedReferenceAssemblyDirectory;
        if (targetFramework is null)
        {
            resolvedReferenceAssemblyDirectory = ResolveConfiguredReferenceAssemblyDirectory(referenceAssemblyDirectory, null);
            targetFramework = InferTargetFrameworkFromReferenceAssemblyDirectory(resolvedReferenceAssemblyDirectory);
        }
        else
        {
            resolvedReferenceAssemblyDirectory = ResolveReferenceAssemblyDirectory(targetFramework, referenceAssemblyDirectory);
        }

        string coreAssemblyPath = Path.Combine(resolvedReferenceAssemblyDirectory, "System.Runtime.dll");
        if (!File.Exists(coreAssemblyPath))
            throw new InvalidOperationException(
                "Modern persisted AOT reference assembly selection requires System.Runtime.dll in "
                + resolvedReferenceAssemblyDirectory);

        string[] referenceAssemblyPaths = PersistedResolverAssemblyPaths(resolvedReferenceAssemblyDirectory);
        PathAssemblyResolver resolver = new(referenceAssemblyPaths);
        MetadataLoadContext metadataLoadContext = new(resolver, "System.Runtime");
        try
        {
            Assembly coreAssembly = metadataLoadContext.LoadFromAssemblyPath(coreAssemblyPath);
            return new PersistedCoreAssemblySelection(
                coreAssembly,
                metadataLoadContext,
                targetFramework,
                resolvedReferenceAssemblyDirectory);
        }
        catch
        {
            metadataLoadContext.Dispose();
            throw;
        }
    }

    private static string NormalizeTargetFrameworkOption(string targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
            return null;

        targetFramework = targetFramework.Trim();
        if (targetFramework.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            FrameworkName frameworkName = new(targetFramework);
            if (!frameworkName.Identifier.Equals(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Modern persisted AOT only supports .NETCoreApp target frameworks.");

            return "net" + frameworkName.Version.Major + "." + frameworkName.Version.Minor;
        }

        return targetFramework;
    }

    private static string ResolveReferenceAssemblyDirectory(string targetFramework, string configuredReferenceAssemblyDirectory)
    {
        string referenceTfm = ReferenceAssemblyTfm(targetFramework);
        if (!string.IsNullOrWhiteSpace(configuredReferenceAssemblyDirectory))
            return ResolveConfiguredReferenceAssemblyDirectory(configuredReferenceAssemblyDirectory, referenceTfm);

        return ResolveInstalledReferenceAssemblyDirectory(referenceTfm);
    }

    private static string ResolveConfiguredReferenceAssemblyDirectory(string configuredReferenceAssemblyDirectory, string referenceTfm)
    {
        if (string.IsNullOrWhiteSpace(configuredReferenceAssemblyDirectory))
            throw new InvalidOperationException("Modern persisted AOT reference assembly path is empty.");

        string fullPath = Path.GetFullPath(configuredReferenceAssemblyDirectory);
        if (File.Exists(fullPath))
            fullPath = Path.GetDirectoryName(fullPath);

        string directDirectory = TryReferenceAssemblyDirectory(fullPath);
        if (directDirectory is not null)
            return directDirectory;

        if (referenceTfm is not null)
        {
            string[] directCandidates =
            [
                Path.Combine(fullPath, "ref", referenceTfm),
                Path.Combine(fullPath, referenceTfm)
            ];

            foreach (string candidate in directCandidates)
            {
                string directory = TryReferenceAssemblyDirectory(candidate);
                if (directory is not null)
                    return directory;
            }

            if (Directory.Exists(fullPath))
            {
                foreach (string versionDirectory in Directory.GetDirectories(fullPath))
                {
                    string directory = TryReferenceAssemblyDirectory(Path.Combine(versionDirectory, "ref", referenceTfm));
                    if (directory is not null)
                        return directory;
                }
            }
        }

        string expected = referenceTfm is null
            ? "a directory containing System.Runtime.dll"
            : "a directory containing System.Runtime.dll or a Microsoft.NETCore.App.Ref pack containing ref/" + referenceTfm;
        throw new InvalidOperationException(
            "Could not resolve modern persisted AOT reference assemblies from "
            + fullPath
            + "; expected "
            + expected
            + ".");
    }

    private static string ResolveInstalledReferenceAssemblyDirectory(string referenceTfm)
    {
        string bestDirectory = null;
        string bestVersion = null;

        foreach (string dotnetRoot in DotNetRootCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string packRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packRoot))
                continue;

            foreach (string versionDirectory in Directory.GetDirectories(packRoot))
            {
                string candidate = TryReferenceAssemblyDirectory(Path.Combine(versionDirectory, "ref", referenceTfm));
                if (candidate is null)
                    continue;

                string version = new DirectoryInfo(versionDirectory).Name;
                if (bestDirectory is null || ComparePackVersions(version, bestVersion) > 0)
                {
                    bestDirectory = candidate;
                    bestVersion = version;
                }
            }
        }

        if (bestDirectory is not null)
            return bestDirectory;

        throw new InvalidOperationException(
            "Could not find Microsoft.NETCore.App.Ref reference assemblies for "
            + referenceTfm
            + ". Set :aot-reference-assembly-path to the target ref assembly directory.");
    }

    private static IEnumerable<string> DotNetRootCandidates()
    {
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            DirectoryInfo runtimeVersionDirectory = new(runtimeDirectory);
            DirectoryInfo dotnetRoot = runtimeVersionDirectory.Parent?.Parent?.Parent;
            if (dotnetRoot is not null)
                yield return dotnetRoot.FullName;
        }

        foreach (string envVar in new[] { "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" })
        {
            string candidate = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(candidate))
                yield return candidate;
        }
    }

    private static string[] PersistedResolverAssemblyPaths(string referenceAssemblyDirectory)
    {
        List<string> paths = new(Directory.GetFiles(referenceAssemblyDirectory, "*.dll"));
        HashSet<string> seenPaths = new(paths, StringComparer.OrdinalIgnoreCase);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!ShouldAddLoadedAssemblyToPersistedResolver(assembly, referenceAssemblyDirectory))
                continue;

            string location = Path.GetFullPath(assembly.Location);
            if (seenPaths.Add(location))
                paths.Add(location);
        }

        return paths.ToArray();
    }

    private static bool ShouldAddLoadedAssemblyToPersistedResolver(Assembly assembly, string referenceAssemblyDirectory)
    {
        if (assembly is null || assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
            return false;

        string assemblyName = assembly.GetName().Name;
        if (assemblyName == typeof(object).Assembly.GetName().Name)
            return false;

        if (File.Exists(Path.Combine(referenceAssemblyDirectory, assemblyName + ".dll")))
            return false;

        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrWhiteSpace(runtimeDirectory)
            && Path.GetFullPath(assembly.Location).StartsWith(Path.GetFullPath(runtimeDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string TryReferenceAssemblyDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        return File.Exists(Path.Combine(directory, "System.Runtime.dll"))
            ? Path.GetFullPath(directory)
            : null;
    }

    private static int ComparePackVersions(string left, string right)
    {
        Version leftVersion = ParsePackVersion(left);
        Version rightVersion = ParsePackVersion(right);
        int versionComparison = leftVersion.CompareTo(rightVersion);
        return versionComparison != 0
            ? versionComparison
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static Version ParsePackVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new Version(0, 0);

        int suffixIndex = version.IndexOf('-');
        if (suffixIndex >= 0)
            version = version.Substring(0, suffixIndex);

        return Version.TryParse(version, out Version parsed)
            ? parsed
            : new Version(0, 0);
    }

    private static string InferTargetFrameworkFromReferenceAssemblyDirectory(string referenceAssemblyDirectory)
    {
        string directoryName = new DirectoryInfo(referenceAssemblyDirectory).Name;
        return ReferenceAssemblyTfm(directoryName);
    }

    private static string ReferenceAssemblyTfm(string targetFramework)
    {
        return "net" + ExtractNetCoreAppVersion(targetFramework);
    }

    private static string ExtractNetCoreAppVersion(string targetFramework)
    {
        targetFramework = NormalizeTargetFrameworkOption(targetFramework);
        if (targetFramework is null)
            throw new InvalidOperationException("Modern persisted AOT target framework is empty.");

        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Modern persisted AOT target framework must be a .NETCoreApp TFM such as net9.0.");

        int start = 3;
        int end = start;
        while (end < targetFramework.Length
            && (char.IsDigit(targetFramework[end]) || targetFramework[end] == '.'))
        {
            end++;
        }

        string version = targetFramework.Substring(start, end - start);
        string[] parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException(
                "Modern persisted AOT target framework must include a major and minor version, e.g. net9.0.");

        return parts[0] + "." + parts[1];
    }

    internal Type ResolvePersistedTypeReference(Type type)
    {
        if (!_isPersistable || _persistedMetadataLoadContext is null || type is null)
            return type;

        lock (_persistedTypeReferences)
        {
            return ResolvePersistedTypeReferenceCore(type);
        }
    }

    internal MethodInfo ResolvePersistedMethodReference(MethodInfo method)
    {
        if (!_isPersistable || _persistedMetadataLoadContext is null || method is null || method is MethodBuilder)
            return method;

        return (MethodInfo)ResolvePersistedMemberReference(method, ResolvePersistedMethodReferenceUncached);
    }

    internal ConstructorInfo ResolvePersistedConstructorReference(ConstructorInfo constructor)
    {
        if (!_isPersistable || _persistedMetadataLoadContext is null || constructor is null || constructor is ConstructorBuilder)
            return constructor;

        return (ConstructorInfo)ResolvePersistedMemberReference(constructor, ResolvePersistedConstructorReferenceUncached);
    }

    internal FieldInfo ResolvePersistedFieldReference(FieldInfo field)
    {
        if (!_isPersistable || _persistedMetadataLoadContext is null || field is null || field is FieldBuilder)
            return field;

        return (FieldInfo)ResolvePersistedMemberReference(field, ResolvePersistedFieldReferenceUncached);
    }

    private MemberInfo ResolvePersistedMemberReference(MemberInfo member, Func<MemberInfo, MemberInfo> resolveUncached)
    {
        lock (_persistedMemberReferences)
        {
            if (_persistedMemberReferences.TryGetValue(member, out MemberInfo mapped))
                return mapped;

            _persistedMemberReferences[member] = member;
            mapped = resolveUncached(member) ?? member;
            _persistedMemberReferences[member] = mapped;
            return mapped;
        }
    }

    private Type ResolvePersistedTypeReferenceCore(Type type)
    {
        if (_persistedTypeReferences.TryGetValue(type, out Type mapped))
            return mapped;

        _persistedTypeReferences[type] = type;
        mapped = ResolvePersistedTypeReferenceUncached(type) ?? type;
        _persistedTypeReferences[type] = mapped;
        return mapped;
    }

    private Type ResolvePersistedTypeReferenceUncached(Type type)
    {
        if (type is null || type is TypeBuilder || type.Assembly is PersistedAssemblyBuilder || type.IsGenericParameter)
            return type;

        if (type.HasElementType)
        {
            Type elementType = ResolvePersistedTypeReferenceCore(type.GetElementType());
            if (ReferenceEquals(elementType, type.GetElementType()))
                return type;

            if (type.IsByRef)
                return elementType.MakeByRefType();
            if (type.IsPointer)
                return elementType.MakePointerType();
            if (type.IsArray)
                return type.IsSZArray ? elementType.MakeArrayType() : elementType.MakeArrayType(type.GetArrayRank());

            return type;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            Type genericDefinition = ResolvePersistedTypeReferenceCore(type.GetGenericTypeDefinition());
            Type[] genericArguments = ResolvePersistedTypeReferences(type.GetGenericArguments());

            if (ReferenceEquals(genericDefinition, type.GetGenericTypeDefinition())
                && TypesReferenceEqual(genericArguments, type.GetGenericArguments()))
                return type;

            return genericDefinition.MakeGenericType(genericArguments);
        }

        Assembly assembly = ResolvePersistedAssemblyReference(type.Assembly);
        if (assembly is null)
            return type;

        return assembly.GetType(type.FullName, throwOnError: false, ignoreCase: false) ?? type;
    }

    private Type[] ResolvePersistedTypeReferences(Type[] types)
    {
        if (types is null)
            return null;

        Type[] resolved = null;
        for (int i = 0; i < types.Length; i++)
        {
            Type type = ResolvePersistedTypeReferenceCore(types[i]);
            if (!ReferenceEquals(type, types[i]))
            {
                resolved ??= (Type[])types.Clone();
                resolved[i] = type;
            }
        }

        return resolved ?? types;
    }

    private Assembly ResolvePersistedAssemblyReference(Assembly assembly)
    {
        if (assembly is null || assembly is AssemblyBuilder || assembly.IsDynamic)
            return null;

        if (ReferenceEquals(assembly, _persistedCoreAssembly))
            return assembly;

        string assemblyName = assembly.GetName().Name;
        if (assemblyName == typeof(object).Assembly.GetName().Name)
            return _persistedCoreAssembly;

        if (string.IsNullOrWhiteSpace(_persistedReferenceAssemblyDirectory))
            return null;

        if (_persistedReferenceAssemblies.TryGetValue(assemblyName, out Assembly mappedAssembly))
            return mappedAssembly;

        string referenceAssemblyPath = Path.Combine(_persistedReferenceAssemblyDirectory, assemblyName + ".dll");
        if (!File.Exists(referenceAssemblyPath))
        {
            if (!ShouldAddLoadedAssemblyToPersistedResolver(assembly, _persistedReferenceAssemblyDirectory))
                return null;

            referenceAssemblyPath = assembly.Location;
        }

        mappedAssembly = _persistedMetadataLoadContext.LoadFromAssemblyPath(referenceAssemblyPath);
        _persistedReferenceAssemblies[assemblyName] = mappedAssembly;
        return mappedAssembly;
    }

    private MemberInfo ResolvePersistedMethodReferenceUncached(MemberInfo member)
    {
        MethodInfo method = (MethodInfo)member;
        Type declaringType = ResolvePersistedTypeReferenceCore(method.DeclaringType);
        if (ReferenceEquals(declaringType, method.DeclaringType))
            return method;

        MethodInfo methodDefinition = method.IsGenericMethod && !method.IsGenericMethodDefinition
            ? method.GetGenericMethodDefinition()
            : method;

        MethodInfo mappedMethod = declaringType
            .GetMethods(AllMemberBindings)
            .FirstOrDefault(candidate => MethodMatches(methodDefinition, candidate));

        if (mappedMethod is null)
            return method;

        if (method.IsGenericMethod && !method.IsGenericMethodDefinition)
            mappedMethod = mappedMethod.MakeGenericMethod(ResolvePersistedTypeReferences(method.GetGenericArguments()));

        return mappedMethod;
    }

    private MemberInfo ResolvePersistedConstructorReferenceUncached(MemberInfo member)
    {
        ConstructorInfo constructor = (ConstructorInfo)member;
        Type declaringType = ResolvePersistedTypeReferenceCore(constructor.DeclaringType);
        if (ReferenceEquals(declaringType, constructor.DeclaringType))
            return constructor;

        return declaringType
            .GetConstructors(AllMemberBindings)
            .FirstOrDefault(candidate => ParametersMatch(constructor.GetParameters(), candidate.GetParameters()))
            ?? constructor;
    }

    private MemberInfo ResolvePersistedFieldReferenceUncached(MemberInfo member)
    {
        FieldInfo field = (FieldInfo)member;
        Type declaringType = ResolvePersistedTypeReferenceCore(field.DeclaringType);
        if (ReferenceEquals(declaringType, field.DeclaringType))
            return field;

        return declaringType
            .GetFields(AllMemberBindings)
            .FirstOrDefault(candidate =>
                candidate.Name == field.Name
                && candidate.IsStatic == field.IsStatic
                && TypeReferencesMatch(ResolvePersistedTypeReferenceCore(field.FieldType), candidate.FieldType))
            ?? field;
    }

    private static readonly BindingFlags AllMemberBindings =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private bool MethodMatches(MethodInfo source, MethodInfo candidate)
    {
        if (candidate.Name != source.Name
            || candidate.IsStatic != source.IsStatic
            || candidate.IsGenericMethodDefinition != source.IsGenericMethodDefinition
            || candidate.GetGenericArguments().Length != source.GetGenericArguments().Length)
        {
            return false;
        }

        if (!TypeReferencesMatch(ResolvePersistedTypeReferenceCore(source.ReturnType), candidate.ReturnType))
            return false;

        return ParametersMatch(source.GetParameters(), candidate.GetParameters());
    }

    private bool ParametersMatch(ParameterInfo[] source, ParameterInfo[] candidate)
    {
        if (source.Length != candidate.Length)
            return false;

        for (int i = 0; i < source.Length; i++)
        {
            Type sourceType = ResolvePersistedTypeReferenceCore(source[i].ParameterType);
            if (!TypeReferencesMatch(sourceType, candidate[i].ParameterType))
                return false;
        }

        return true;
    }

    private static bool TypeReferencesMatch(Type left, Type right)
    {
        if (ReferenceEquals(left, right) || left == right)
            return true;

        if (left is null || right is null)
            return false;

        if (left.IsGenericParameter || right.IsGenericParameter)
            return left.IsGenericParameter
                && right.IsGenericParameter
                && left.GenericParameterPosition == right.GenericParameterPosition;

        if (left.HasElementType || right.HasElementType)
        {
            return left.HasElementType
                && right.HasElementType
                && left.IsArray == right.IsArray
                && left.IsByRef == right.IsByRef
                && left.IsPointer == right.IsPointer
                && (!left.IsArray || left.GetArrayRank() == right.GetArrayRank())
                && TypeReferencesMatch(left.GetElementType(), right.GetElementType());
        }

        if (left.IsGenericType || right.IsGenericType)
        {
            if (!left.IsGenericType || !right.IsGenericType)
                return false;

            Type leftDefinition = left.IsGenericTypeDefinition ? left : left.GetGenericTypeDefinition();
            Type rightDefinition = right.IsGenericTypeDefinition ? right : right.GetGenericTypeDefinition();
            if (!TypeReferencesMatch(leftDefinition, rightDefinition))
                return false;

            Type[] leftArguments = left.GetGenericArguments();
            Type[] rightArguments = right.GetGenericArguments();
            if (leftArguments.Length != rightArguments.Length)
                return false;

            for (int i = 0; i < leftArguments.Length; i++)
            {
                if (!TypeReferencesMatch(leftArguments[i], rightArguments[i]))
                    return false;
            }

            return true;
        }

        return string.Equals(left.FullName, right.FullName, StringComparison.Ordinal)
            && string.Equals(left.Assembly.GetName().Name, right.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TypesReferenceEqual(Type[] left, Type[] right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

#endif


    public string SaveAssembly()
    {
        if (!_isPersistable)
        {
            throw new InvalidOperationException("Assembly is not persistable");
        }

#if NETFRAMEWORK
        var savePath = Path.Combine(_outDir, _outFileName);
        _myAssembly.Save(savePath, PortableExecutableKinds.ILOnly, ImageFileMachine.I386);
        return savePath;
#elif NET9_0_OR_GREATER
        var savePath = Path.Combine(_outDir, _outFileName);
        try
        {
            if ( _entryPointMethodBuilder is not null || _docWriter is not null)
                SavePersistedAssemblyHard(savePath);
            else
                ((PersistedAssemblyBuilder)_myAssembly).Save(savePath);
        }
        finally
        {
            _persistedMetadataLoadContext?.Dispose();
        }
        return savePath;
#else
        return null;
#endif

    }

#if NET9_0_OR_GREATER
    private void SavePersistedAssemblyHard(string savePath )
    {
        PersistedAssemblyBuilder ab = (PersistedAssemblyBuilder)_myAssembly;
        MetadataBuilder metadataBuilder = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData, out MetadataBuilder pdbBuilder);
            
        MethodDefinitionHandle entryPointHandle = 
            _entryPointMethodBuilder is null 
            ? default
            : MetadataTokens.MethodDefinitionHandle(_entryPointMethodBuilder.MetadataToken);
        DebugDirectoryBuilder debugDirectoryBuilder = _docWriter is null
            ? null
            : GeneratePdb(pdbBuilder, metadataBuilder.GetRowCounts(), entryPointHandle, Path.ChangeExtension(_outFileName, ".pdb"));

        ManagedPEBuilder peBuilder = new(
                    header: _entryPointMethodBuilder is null
                        ? PEHeaderBuilder.CreateLibraryHeader()
                        : PEHeaderBuilder.CreateExecutableHeader(),
                    metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
                    ilStream: ilStream,
                    mappedFieldData: fieldData,
                    debugDirectoryBuilder: debugDirectoryBuilder,
                    entryPoint: entryPointHandle,
                    deterministicIdProvider: ComputeDeterministicId);

        BlobBuilder peBlob = new();
        peBuilder.Serialize(peBlob);

        // Create the executable:
        using FileStream fileStream = new(savePath, FileMode.Create, FileAccess.Write);
        peBlob.WriteContentTo(fileStream);
    }

    static DebugDirectoryBuilder GeneratePdb(
        MetadataBuilder pdbBuilder,
        ImmutableArray<int> rowCounts,
        MethodDefinitionHandle entryPointHandle,
        string pdbPath)
    {
        BlobBuilder portablePdbBlob = new BlobBuilder();
        PortablePdbBuilder portablePdbBuilder = new PortablePdbBuilder(
            pdbBuilder,
            rowCounts,
            entryPointHandle,
            ComputeDeterministicId);
        BlobContentId pdbContentId = portablePdbBuilder.Serialize(portablePdbBlob);

        DebugDirectoryBuilder debugDirectoryBuilder = new DebugDirectoryBuilder();
        debugDirectoryBuilder.AddCodeViewEntry(pdbPath, pdbContentId, portablePdbBuilder.FormatVersion);
        debugDirectoryBuilder.AddEmbeddedPortablePdbEntry(portablePdbBlob, portablePdbBuilder.FormatVersion);
        debugDirectoryBuilder.AddPdbChecksumEntry("SHA256", ImmutableArray.CreateRange(SHA256.HashData(portablePdbBlob.ToArray())));
        debugDirectoryBuilder.AddReproducibleEntry();
        return debugDirectoryBuilder;
    }

    private static BlobContentId ComputeDeterministicId(IEnumerable<Blob> blobs)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Blob blob in blobs)
            hash.AppendData(blob.GetBytes());

        return BlobContentId.FromHash(hash.GetHashAndReset());
    }
#endif


#if NETFRAMEWORK
    internal void SetEntryPoint(MethodInfo mi, PEFileKinds kind)
    {
        _myAssembly.SetEntryPoint(mi, kind);
    }
#elif NET9_0_OR_GREATER
    internal void SetEntryPoint(MethodBuilder mb)
    {
        _entryPointMethodBuilder = mb;
    }
#endif


    public TypeBuilder DefinePublicType(string name, Type parent, bool preserveName)
    {
        return DefineType(name, parent, TypeAttributes.Public, preserveName);
    }

    internal TypeBuilder DefineType(string name, Type parent, TypeAttributes attr, bool preserveName)
    {
        ContractUtils.RequiresNotNull(name, nameof(name));
        ContractUtils.RequiresNotNull(parent, nameof(parent));

        StringBuilder sb = new StringBuilder(name);
        if (!preserveName)
        {
            int index = Interlocked.Increment(ref _index);
            sb.Append("$");
            sb.Append(index);
        }

        // There is a bug in Reflection.Emit that leads to 
        // Unhandled Exception: System.Runtime.InteropServices.COMException (0x80131130): Record not found on lookup.
        // if there is any of the characters []*&+,\ in the type name and a method defined on the type is called.
        sb.Replace('+', '_').Replace('[', '_').Replace(']', '_').Replace('*', '_').Replace('&', '_').Replace(',', '_').Replace('\\', '_');

        name = sb.ToString();

#if NET9_0_OR_GREATER
        parent = ResolvePersistedTypeReference(parent);
#endif
        return _myModule.DefineType(name, attr, parent);
    }


    private const MethodAttributes CtorAttributes = MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public;
    private const MethodImplAttributes ImplAttributes = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
    private const MethodAttributes InvokeAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual;
    private const TypeAttributes DelegateAttributes = TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoClass;
    private static readonly Type[] _DelegateCtorSignature = new Type[] { typeof(object), typeof(IntPtr) };

    public Type MakeDelegateType(string name, Type[] parameters, Type returnType)
    {
        TypeBuilder builder = DefineType(name, typeof(MulticastDelegate), DelegateAttributes, false);
#if NET9_0_OR_GREATER
        parameters = ResolvePersistedTypeReferences(parameters);
        returnType = ResolvePersistedTypeReference(returnType);
        Type[] delegateCtorSignature = ResolvePersistedTypeReferences(_DelegateCtorSignature);
#else
        Type[] delegateCtorSignature = _DelegateCtorSignature;
#endif
        builder.DefineConstructor(CtorAttributes, CallingConventions.Standard, delegateCtorSignature).SetImplementationFlags(ImplAttributes);
        builder.DefineMethod("Invoke", InvokeAttributes, returnType, parameters).SetImplementationFlags(ImplAttributes);
        return builder.CreateTypeInfo();
    }

}
