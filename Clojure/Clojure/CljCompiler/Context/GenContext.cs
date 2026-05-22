/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

using clojure.lang.Runtime;
using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using System.Collections.Concurrent;
using clojure.lang.CljCompiler.Ast;

#if NETFRAMEWORK
using AssemblyGenT = Microsoft.Scripting.Generation.AssemblyGen;
#else
using AssemblyGenT = clojure.lang.CljCompiler.Context.MyAssemblyGen;
#endif


namespace clojure.lang.CljCompiler.Context
{
    public sealed class GenContext
    {
        #region Data

        readonly AssemblyGenT _assyGen;
        public AssemblyGenT AssemblyGen
        {
            get { return _assyGen; }
        }

        public AssemblyBuilder AssemblyBuilder
        {
            get { return _assyGen.AssemblyBuilder; }
        }

        readonly ModuleBuilder _moduleBuilder;
        public ModuleBuilder ModuleBuilder
        {
            get { return _moduleBuilder; }
        }

        DynInitHelper _dynInitHelper;
        internal DynInitHelper DynInitHelper
        {
            get { return _dynInitHelper; }
        }

        readonly bool _isDebuggable;
        public bool IsDebuggable
        {
            get { return _isDebuggable; }
        }

        readonly ISymbolDocumentWriter _docWriter = null;
        public ISymbolDocumentWriter DocWriter
        {
            get { return _docWriter; }
        }

        readonly SymbolDocumentInfo _docInfo;
        public SymbolDocumentInfo DocInfo { get { return _docInfo; } }

        TypeBuilder _tb;
        public TypeBuilder TB { get { return _tb; } }

        readonly string _sourceName;
        readonly GeneratedArtifactRegistry _generatedArtifacts;
        public GeneratedArtifactRegistry GeneratedArtifacts
        {
            get { return _generatedArtifacts; }
        }

        public GeneratedArtifactBackend ArtifactBackend { get; }
        public GenerationContextPair GenerationContexts { get; private set; }

        public bool CanRunNow
        {
            get
            {
#if NET9_0_OR_GREATER
                return ArtifactBackend != GeneratedArtifactBackend.Persisted;
#else
                return true;
#endif
            }
        }

        public bool CanPersist
        {
            get { return ArtifactBackend == GeneratedArtifactBackend.Persisted; }
        }

#if NET9_0_OR_GREATER
        public Assembly PersistedCoreAssembly
        {
            get { return CanPersist ? _assyGen.PersistedCoreAssembly : null; }
        }

        public bool UsesSameRuntimePersistedCoreAssembly
        {
            get { return CanPersist && Object.ReferenceEquals(PersistedCoreAssembly, typeof(object).Assembly); }
        }
#endif


        public string Path { get; set; }

        #endregion

        #region C-tors & factory methods

        private class DefaultAssemblyComparer : IEqualityComparer<Assembly>
        {
            public bool Equals(Assembly x, Assembly y) => Object.ReferenceEquals(x, y);
            public int GetHashCode(Assembly obj) => obj.GetHashCode();
        }

        private readonly static ConcurrentDictionary<Assembly, bool> _internalAssemblies = new ConcurrentDictionary<Assembly, bool>(new DefaultAssemblyComparer());
        public static bool IsInternalAssembly(Assembly a) => a is not null && _internalAssemblies.ContainsKey(a);

        private static void AddInternalAssembly(GenContext ctx)
        {
            // Sometime we are looking up via the AssemblyBuilder.
            // But sometimes we are accessing via a method on a type. The defining assembly is actually a RuntimeAssembly, not the AssemblyBuilder itself.
            // I'm not sure how else to find out what that RuntimeAssembly is, other than creating a type and a method and getting its assembly.
            // We'll then register both the RuntimeAssembly and the AssemblyBuilder.

            var module = ctx.ModuleBuilder;
            var dummyType = module.DefineType("__________________DummyType");
            var method = dummyType.DefineMethod("__________________DummyMethod", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ret);

            dummyType.CreateType();

            var runtimeMethod = dummyType.GetMethod("__________________DummyMethod");
            var runtimeAssembly = runtimeMethod.DeclaringType.Assembly;

            _internalAssemblies[ctx.AssemblyBuilder] = true;
            _internalAssemblies[runtimeAssembly] = true;
        }





        enum AssemblyType { Internal, External }

        public static GenContext CreateWithInternalAssembly(string assyName, bool createDynInitHelper)
        {
            GeneratedArtifactRegistry generatedArtifacts = new();
            GenContext ctx = CreateGenContext(AssemblyType.Internal, assyName, assyName, ".dll", null, createDynInitHelper, generatedArtifacts);
            AddInternalAssembly(ctx);
            GenerationContextPair.CreateEvalOnly(ctx);
            return ctx;
        }

        // Doesn't appear to be used.
        //public static GenContext CreateWithExternalAssembly(string sourceName, AssemblyName assemblyName, string extension, bool createDynInitHelper)
        //{
        //    string path = Compiler.CompilePathVar.deref() as string;
        //    return new GenContext(path ?? System.IO.Directory.GetCurrentDirectory(), assemblyName, extension, createDynInitHelper, sourceName);
        //}

        public static GenContext CreateWithExternalAssembly(string sourceName, string assyName, string extension, bool createDynInitHelper)
        {
            string path = Compiler.CompilePathVar.deref() as string;
            GeneratedArtifactRegistry generatedArtifacts = new();
            GenContext persistedContext = CreateGenContext(
                AssemblyType.External,
                sourceName,
                assyName,
                extension,
                path ?? System.IO.Directory.GetCurrentDirectory(),
                createDynInitHelper,
                generatedArtifacts);

            GenContext evalContext = CreateGenContext(
                AssemblyType.Internal,
                sourceName,
                EvalAssemblyName(assyName),
                ".dll",
                null,
                createDynInitHelper,
                generatedArtifacts);

            AddInternalAssembly(evalContext);
            GenerationContextPair.CreatePaired(evalContext, persistedContext);
            return persistedContext;
        }

        public static GenContext CreateWithExternalAssembly(string assyName, string extension, bool createDynInitHelper)
        {
            return CreateWithExternalAssembly(assyName, assyName, extension, createDynInitHelper);
        }

        private static GenContext CreateGenContext(
            AssemblyType assemblyType,
            string sourceName,
            string assyName,
            string extension,
            string directory,
            bool createDynInitHelper,
            GeneratedArtifactRegistry generatedArtifacts)
        {
            if (directory != null)
            {
                if (directory.Length > 0) //&& directory != ".")
                    assyName = assyName.Replace("/", ".");
            }

            AssemblyName aname = new AssemblyName(assyName);
            return new GenContext(assemblyType, directory, aname, extension, createDynInitHelper, sourceName, generatedArtifacts);
        }

        private static string EvalAssemblyName(string assyName)
        {
            string name = string.IsNullOrEmpty(assyName) ? "eval" : assyName;
            return name.Replace('/', '.').Replace('\\', '.').Replace(System.IO.Path.PathSeparator, '.') + "__eval" + RT.nextID();
        }

        private GenContext(
            AssemblyType assemblyType,
            string directory,
            AssemblyName aname,
            string extension,
            bool createDynInitHelper,
            string sourceName,
            GeneratedArtifactRegistry generatedArtifacts)
        {
            // TODO: Make this settable from a *debug* flag
            _sourceName = sourceName;
            _generatedArtifacts = generatedArtifacts ?? new GeneratedArtifactRegistry();
            ArtifactBackend = assemblyType == AssemblyType.Internal
                ? GeneratedArtifactBackend.Eval
                : GeneratedArtifactBackend.Persisted;

            _isDebuggable = ShouldEmitDebugInfo(assemblyType);

#if NETFRAMEWORK || NET9_0_OR_GREATER
            switch (assemblyType)
            {
                case AssemblyType.Internal:
#if NETFRAMEWORK
                    _assyGen = new AssemblyGenT(aname, directory, extension, _isDebuggable);  // The Microsoft version has only the single 4-arg constructor
#else
                    _assyGen = new MyAssemblyGen(aname, _isDebuggable);   // MyAssemblyGen has a two-arg version -- this indicates Internal.
#endif
                    break;
                case AssemblyType.External:
                    _assyGen = new AssemblyGenT(aname, directory, extension, _isDebuggable);
                    break;
                default:
                    throw new InvalidOperationException("Unknown AssemblyType");
            }
#else
            _assyGen = new MyAssemblyGen(aname, _isDebuggable);
#endif

            if (createDynInitHelper)
                _dynInitHelper = new DynInitHelper(this, GenerateName());

            _docInfo = Expression.SymbolDocument(sourceName);

            _moduleBuilder = _assyGen.ModuleBuilder;

            Path = ComputeAssemblyPath(directory, aname.Name, extension);

#if NETFRAMEWORK
            if (_isDebuggable)
            {
                _docWriter = ModuleBuilder.DefineDocument(sourceName, ClojureContext.Default.LanguageGuid, ClojureContext.Default.VendorGuid, Guid.Empty);
                //_assyGen.DocWriter = _docWriter;
            }
#elif NET9_0_OR_GREATER
            if (_isDebuggable && assemblyType == AssemblyType.External)
            {
                _docWriter = ModuleBuilder.DefineDocument(sourceName, ClojureContext.Default.LanguageGuid); 
                _assyGen.SetDocWriter(_docWriter);
            }
#endif
        }

        private string ComputeAssemblyPath(string directory, string name, string extension)
        {
            directory = directory ?? ".";
            directory = System.IO.Path.GetFullPath(directory);
            extension = extension ?? ".dll";
            return System.IO.Path.Combine(directory, name + extension);

        }

        private static bool ShouldEmitDebugInfo(AssemblyType assemblyType)
        {
#if DEBUG
#if NET9_0_OR_GREATER
            return assemblyType != AssemblyType.External;
#else
            return true;
#endif
#else
            return false;
#endif
        }


        internal GenContext WithNewDynInitHelper()
        {
            return WithNewDynInitHelper(GenerateName());
        }

        internal GenContext WithNewDynInitHelper(string dihClassName)
        {
            GenContext newContext = Clone();

            newContext._dynInitHelper = new DynInitHelper(newContext, dihClassName);

            return newContext;
        }

        static string GenerateName()
        {
            return "__InternalDynamicExpressionInits_" + RT.nextID();
        }

        private GenContext Clone()
        {
            return (GenContext)MemberwiseClone();
        }

        internal void AttachGenerationContexts(GenerationContextPair generationContexts)
        {
            if (generationContexts is null)
                throw new ArgumentNullException(nameof(generationContexts));
            if (GenerationContexts is not null && !ReferenceEquals(GenerationContexts, generationContexts))
                throw new InvalidOperationException("Generation contexts were already attached.");

            GenerationContexts = generationContexts;
        }

        public GenContext WithTypeBuilder(TypeBuilder tb)
        {
            GenContext newContext = Clone();
            newContext._tb = tb;
            return newContext;
        }

        public GeneratedTypeRecord DeclareGeneratedType(string logicalName, string runtimeName = null)
        {
            return _generatedArtifacts.DeclareType(
                ArtifactBackend,
                CurrentGeneratedSourcePath(),
                logicalName,
                runtimeName);
        }

        public GeneratedTypeRecord RegisterGeneratedType(string logicalName, string runtimeName, TypeBuilder builder)
        {
            return _generatedArtifacts.RegisterTypeBuilder(
                ArtifactBackend,
                CurrentGeneratedSourcePath(),
                logicalName,
                runtimeName,
                builder);
        }

        public GeneratedTypeRecord RegisterGeneratedType(GeneratedTypeRecord record, string runtimeName, TypeBuilder builder)
        {
            if (record is null)
                throw new ArgumentNullException(nameof(record));

            return _generatedArtifacts.RegisterTypeBuilder(
                ArtifactBackend,
                record.Id,
                runtimeName,
                builder);
        }

        public void RegisterGeneratedTypeCreated(GeneratedTypeRecord record, Type type)
        {
            if (record is null)
                return;

            _generatedArtifacts.RegisterCreatedType(ArtifactBackend, record.Id, type);
        }

        public GeneratedMemberRecord RegisterGeneratedMember(
            GeneratedTypeRecord owner,
            GeneratedMemberKind kind,
            string logicalName,
            MemberInfo member)
        {
            if (owner is null || member is null)
                return null;

            return _generatedArtifacts.RegisterMember(ArtifactBackend, owner.Id, kind, logicalName, member);
        }

        public Type ResolveGeneratedTypeForCurrentBackend(Type type)
        {
            return _generatedArtifacts.ResolveGeneratedTypeForBackend(type, ArtifactBackend);
        }

        private string CurrentGeneratedSourcePath()
        {
            return Compiler.SourcePathVar.deref() as string ?? _sourceName ?? Path ?? string.Empty;
        }

        #endregion

        #region Other

#if NET9_0_OR_GREATER
        internal void SetEntryPoint(MethodBuilder mb)
        {
            _assyGen.SetEntryPoint(mb);
        }
#endif

        // DO not call context.AssmeblyGen.SaveAssembly() directly.
        internal void SaveAssembly()
        {
            if (_dynInitHelper != null)
                _dynInitHelper.FinalizeType();

#if NETFRAMEWORK  || NET9_0_OR_GREATER
            _assyGen.SaveAssembly();
#else
            Console.WriteLine("AOT-compilation not available");
            //var assembly = AssemblyBuilder;
            //var generator = new Lokad.ILPack.AssemblyGenerator();
            //generator.GenerateAssembly(assembly,Path);
#endif
        }

        #endregion

        #region Debug info

        public static Expression AddDebugInfo(Expression expr, IPersistentMap spanMap)
        {
            if (Compiler.CompilerContextVar.deref() is GenContext context)
                return context.MaybeAddDebugInfo(expr, spanMap);
            else
                return expr;
        }

        public Expression MaybeAddDebugInfo(Expression expr, IPersistentMap spanMap)
        {
            if (_isDebuggable && spanMap != null & _docInfo != null)
            {
                if (Compiler.GetLocations(spanMap, out int startLine, out int startCol, out int finishLine, out int finishCol))
                    return AstUtils.AddDebugInfo(expr,
                        _docInfo,
                        new Microsoft.Scripting.SourceLocation(0, startLine, startCol),
                        new Microsoft.Scripting.SourceLocation(0, finishLine, finishCol));
            }
            return expr;
        }

        public static void EmitDebugInfo(CljILGen ilg, IPersistentMap spanMap)
        {
            if (Compiler.CompilerContextVar.deref() is GenContext context)
                context.MaybeEmitDebugInfo(ilg, spanMap);
        }

        public void MaybeEmitDebugInfo(CljILGen ilg, IPersistentMap spanMap)
        {
#if NETFRAMEWORK || NET9_0_OR_GREATER
            if (_docWriter != null && spanMap != null)
            {
                if (Compiler.GetLocations(spanMap, out int startLine, out int startCol, out int finishLine, out int finishCol))
                {
                    try
                    {
                        ilg.ILGenerator.MarkSequencePoint(_docWriter, startLine, startCol, finishLine, finishCol);
                    }
                    catch (NotSupportedException)
                    {
                        // probably a dynamic ilgen
                    }
                }
            }
#endif
        }

        public static void SetLocalName(LocalBuilder lb, string name)
        {
            if (Compiler.CompilerContextVar.deref() is GenContext context)
                context.MaybSetLocalName(lb, name);
        }

        public void MaybSetLocalName(LocalBuilder lb, string name)
        {
#if NETFRAMEWORK
            if (_isDebuggable)
                lb.SetLocalSymInfo(name);
#endif
        }

        #endregion
    }
}
