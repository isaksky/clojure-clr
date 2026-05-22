#if NET9_0_OR_GREATER

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using clojure.lang;
using clojure.lang.CljCompiler.Ast;
using clojure.lang.CljCompiler.Context;
using NUnit.Framework;
using Compiler = clojure.lang.Compiler;

namespace Clojure.Tests.LibTests
{
    [TestFixture]
    [NonParallelizable]
    public class AotRegressionTests
    {
        private const int ProcessTimeoutMilliseconds = 60_000;

        private const string SampleBody = @"
(def answer 41)

(defn inc-answer []
  (inc answer))

(def invoked (inc-answer))

(def named-local-fn (fn helper [] 7))
(def named-local-result (named-local-fn))

(let [x 5]
  (+ x invoked))

(def after-let :loaded)
";

        private const string DebugSymbolsBody = @"(def answer 41)
(defn inc-answer []
  (inc answer))
(def invoked (inc-answer))
";

        private const string ProgressiveMacroBody = @"
(def macro-suffix ""ok"")

(defmacro defprogressive [name base]
  (list 'def name (keyword (str base ""-"" macro-suffix))))

(defprogressive macro-produced ""macro"")
(def later-form-sees-macro-produced (str macro-produced))

(defn later-fn []
  (str later-form-sees-macro-produced ""|"" macro-produced))

(def later-call (later-fn))
(def after-macro :loaded)
";

        private const string DynamicHostInteropBody = @"
(defn stringify-dynamic [x]
  (.ToString x))

(def dynamic-result (stringify-dynamic 42))

(defn dynamic-length [x]
  (.Length x))

(def dynamic-length-result (dynamic-length ""abcd""))
";

        private const string GenDelegateBody = @"
(def delegate-hit (atom false))
(def starter (gen-delegate System.Threading.ThreadStart [] (reset! delegate-hit true)))
";

        private const string GeneratedInterfaceProtocolBody = @"
(defprotocol AotProtocol
  (aot-value [x]))

(extend-protocol AotProtocol
  System.String
  (aot-value [x] (str ""string:"" x)))

(def protocol-result (aot-value ""ok""))
";

        private const string DeftypeReifyBody = @"
(defprotocol AotBoxProtocol
  (box-value [x]))

(deftype AotBox [x]
  AotBoxProtocol
  (box-value [this] x))

(def boxed (->AotBox 41))
(def boxed-value (box-value boxed))
(def boxed-type-name (.FullName AotBox))

(def reified
  (let [prefix ""re""]
    (reify AotBoxProtocol
      (box-value [this] (str prefix ""ify"")))))

(def reified-value (box-value reified))
(def reified-type-name (.FullName (class reified)))
";

        private static readonly string[] RuntimeNamespaceTranche =
        [
            "clojure.walk",
            "clojure.template",
            "clojure.set",
            "clojure.string",
            "clojure.data"
        ];

        private static readonly UnsupportedGeneratedFormCase[] UnsupportedGeneratedFormCases =
        [
            new(
                "gen-class",
                ns => $@"(ns {ns})
(gen-class :name {ns}.GeneratedClass :load-impl-ns false)"),
            new(
                "proxy",
                ns => $@"(ns {ns})
(def writer (proxy [System.IO.StringWriter] []))")
        ];

        [OneTimeSetUp]
        public void Setup()
        {
            RT.Init();
            Compiler.EnsureMacroCheck();
        }

        [Test]
        public void MinimalNamespaceAotProducesPersistedAssembly()
        {
            using AotSample sample = AotSample.Create();
            CompileSample(sample);

            Assert.That(File.Exists(sample.AssemblyPath), Is.True, "AOT compilation should persist the namespace DLL.");

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            Type initType = assembly.GetType(sample.InitTypeName);
            Assert.That(initType, Is.Not.Null, "Persisted assembly should contain the namespace initializer type.");

            MethodInfo initialize = initType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            Assert.That(initialize, Is.Not.Null, "Namespace initializer should expose public static Initialize().");

            Assert.That(Var.find(Symbol.intern(sample.NamespaceName, "invoked")).deref(), Is.EqualTo(42));
            Assert.That(Var.find(Symbol.intern(sample.NamespaceName, "named-local-result")).deref(), Is.EqualTo(7));
            Assert.That(Var.find(Symbol.intern(sample.NamespaceName, "after-let")).deref(), Is.EqualTo(Keyword.intern(null, "loaded")));
        }

        [Test]
        public void MinimalNamespaceAotPassesReflectionInspection()
        {
            using AotSample sample = AotSample.Create();
            CompileSample(sample);

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            AssemblyName[] references = assembly.GetReferencedAssemblies();
            string[] referenceNames = references.Select(r => r.Name).ToArray();
            string[] typeNames = assembly.GetTypes().Select(t => t.FullName).ToArray();

            Assert.That(referenceNames, Does.Contain("Clojure"), "Persisted namespace should reference the Clojure runtime.");
            Assert.That(references.Any(IsEvalOrInternalDynamicReference), Is.False,
                "Persisted namespace must not reference transient eval/internal dynamic assemblies.");
            Assert.That(typeNames, Does.Contain(sample.InitTypeName), "Persisted assembly should contain the namespace initializer type.");
            Assert.That(typeNames, Does.Contain(sample.NamespaceName + "$inc_answer"),
                "Persisted assembly should contain the generated defn function class.");

            Type initType = assembly.GetType(sample.InitTypeName);
            MethodInfo initialize = initType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            Assert.That(initialize, Is.Not.Null, "Namespace initializer should expose public static Initialize().");
        }

        [Test]
        public void MinimalNamespaceAotSuppressesDirectLinkingForPersistedCompile()
        {
            using AotSample sample = AotSample.Create();
            CompileSample(sample, directLinking: true);

            Var incAnswer = Var.find(Symbol.intern(sample.NamespaceName, "inc-answer"));
            Assert.That(incAnswer, Is.Not.Null, "Compiled namespace should define inc-answer.");
            Assert.That(Compiler.TryGetDirectLink(incAnswer, out _), Is.False,
                "Modern persisted AOT suppresses direct-link records until generated types are backend-aware.");

            Assert.That(File.Exists(sample.AssemblyPath), Is.True, "AOT compilation should still persist the namespace DLL.");
            Assert.That(Var.find(Symbol.intern(sample.NamespaceName, "invoked")).deref(), Is.EqualTo(42));
        }

        [Test]
        public void ModernPersistedAotSupportsDynamicHostInteropCallSites()
        {
            using AotSample sample = AotSample.Create(DynamicHostInteropBody);
            GenContext context = CompileSampleWithExplicitContext(sample);

            Assert.That(VarValue(sample, "dynamic-result"), Is.EqualTo("42"));
            Assert.That(VarValue(sample, "dynamic-length-result"), Is.EqualTo(4));

            SaveExplicitContext(context);
            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            Assert.That(assembly.GetReferencedAssemblies().Any(IsEvalOrInternalDynamicReference), Is.False,
                "Persisted dynamic host interop should not reference transient eval/internal dynamic assemblies.");

            Assert.That(context.GeneratedArtifacts.Types.Any(type =>
                IsDynamicHostInteropHelper(type.GetRuntimeName(GeneratedArtifactBackend.Persisted))
                && IsDynamicHostInteropHelper(type.GetRuntimeName(GeneratedArtifactBackend.Eval))), Is.True,
                "Dynamic host interop helpers should be paired across persisted and eval backends."
                + Environment.NewLine
                + DumpGeneratedTypes(context));
        }

        [Test]
        public void ModernPersistedAotSupportsRuntimeGenDelegateWrappers()
        {
            using AotSample sample = AotSample.Create(GenDelegateBody);
            CompileSample(sample);

            ThreadStart starter = (ThreadStart)VarValue(sample, "starter");
            starter();

            Assert.That(((IDeref)VarValue(sample, "delegate-hit")).deref(), Is.True);

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            Assert.That(assembly.GetReferencedAssemblies().Any(IsEvalOrInternalDynamicReference), Is.False,
                "Persisted namespace should not reference the runtime-only delegate wrapper assembly.");
        }

        [Test]
        public void ModernPersistedAotPairsGeneratedInterfacesAcrossBackends()
        {
            using AotSample sample = AotSample.Create(GeneratedInterfaceProtocolBody);
            GenContext context = CompileSampleWithExplicitContext(sample);

            Assert.That(VarValue(sample, "protocol-result"), Is.EqualTo("string:ok"));

            string interfaceName = sample.NamespaceName + ".AotProtocol";
            GeneratedTypeRecord interfaceType = context.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.Id.LogicalName == "gen-interface:" + interfaceName);

            Assert.That(interfaceType, Is.Not.Null,
                "Generated protocol interface should be recorded as a paired generated artifact.");
            Assert.That(interfaceType.GetRuntimeName(GeneratedArtifactBackend.Persisted), Is.EqualTo(interfaceName));
            Assert.That(interfaceType.GetRuntimeName(GeneratedArtifactBackend.Eval), Is.EqualTo(interfaceName));
            Assert.That(interfaceType.GetCreatedType(GeneratedArtifactBackend.Persisted), Is.Not.Null);
            Assert.That(interfaceType.GetCreatedType(GeneratedArtifactBackend.Eval), Is.Not.Null);
            Assert.That(interfaceType.Members.Values.Any(member =>
                member.Id.Kind == GeneratedMemberKind.Method
                && member.Id.LogicalName == "aot_value"
                && member.PersistedMember is MethodInfo
                && member.EvalMember is MethodInfo), Is.True,
                "Generated protocol interface method should be recorded for both backends.");

            SaveExplicitContext(context);
            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            Assert.That(assembly.GetType(interfaceName), Is.Not.Null,
                "Persisted namespace assembly should contain the generated protocol interface.");
            Assert.That(assembly.GetReferencedAssemblies().Any(IsEvalOrInternalDynamicReference), Is.False,
                "Generated protocol interface should not introduce transient dynamic assembly references.");
        }

        [Test]
        public void ModernPersistedAotSupportsDeftypeAndReifyGeneratedTypes()
        {
            using AotSample sample = AotSample.Create(DeftypeReifyBody);
            GenContext context = CompileSampleWithExplicitContext(sample);

            Assert.That(VarValue(sample, "boxed-value"), Is.EqualTo(41));
            Assert.That(VarValue(sample, "boxed-type-name"), Is.EqualTo(sample.NamespaceName + ".AotBox"));
            Assert.That(VarValue(sample, "reified-value"), Is.EqualTo("reify"));
            Assert.That((string)VarValue(sample, "reified-type-name"), Does.Contain("$reify__"));

            GeneratedTypeRecord deftypeType = context.GeneratedArtifacts.Types.SingleOrDefault(
                type => type.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.NamespaceName + ".AotBox");
            GeneratedTypeRecord deftypeBaseType = context.GeneratedArtifacts.Types.SingleOrDefault(
                type => type.Id.LogicalName == "deftype-base:" + sample.NamespaceName + ".AotBox");
            GeneratedTypeRecord reifyType = context.GeneratedArtifacts.Types.FirstOrDefault(
                type => IsReifyRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Persisted))
                    && IsReifyRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Eval))
                    && HasPersistedMember(type, GeneratedMemberKind.Method, "box_value"));
            GeneratedTypeRecord reifyBaseType = context.GeneratedArtifacts.Types.FirstOrDefault(
                type => type.Id.LogicalName.StartsWith("deftype-base:", StringComparison.Ordinal)
                    && IsReifyRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Persisted))
                    && IsReifyRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Eval))
                    && HasPersistedMember(type, GeneratedMemberKind.Method, "box_value"));

            Assert.That(deftypeType, Is.Not.Null,
                "deftype main class should be paired across persisted and eval backends."
                + Environment.NewLine
                + DumpGeneratedTypes(context));
            Assert.That(deftypeBaseType, Is.Not.Null,
                "deftype base class should be paired across persisted and eval backends."
                + Environment.NewLine
                + DumpGeneratedTypes(context));
            Assert.That(reifyType, Is.Not.Null,
                "reify main class should be paired across persisted and eval backends."
                + Environment.NewLine
                + DumpGeneratedTypes(context));
            Assert.That(reifyBaseType, Is.Not.Null,
                "reify base class should be paired across persisted and eval backends."
                + Environment.NewLine
                + DumpGeneratedTypes(context));

            Assert.That(HasPersistedMember(deftypeType, GeneratedMemberKind.Constructor, ".ctor"), Is.True);
            Assert.That(HasPersistedMember(deftypeType, GeneratedMemberKind.Method, "box_value"), Is.True);
            Assert.That(HasPersistedMember(deftypeBaseType, GeneratedMemberKind.Field, "x"), Is.True);
            Assert.That(HasPersistedMember(reifyType, GeneratedMemberKind.Constructor, ".ctor"), Is.True);
            Assert.That(HasPersistedMember(reifyType, GeneratedMemberKind.Method, "box_value"), Is.True);

            SaveExplicitContext(context);
            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            string[] typeNames = assembly.GetTypes().Select(type => type.FullName).ToArray();

            Assert.That(typeNames, Does.Contain(sample.NamespaceName + ".AotBox"),
                "Persisted assembly should contain the generated deftype class.");
            Assert.That(typeNames.Any(IsReifyRuntimeName), Is.True,
                "Persisted assembly should contain the generated reify class.");
            Assert.That(assembly.GetReferencedAssemblies().Any(IsEvalOrInternalDynamicReference), Is.False,
                "deftype/reify persisted output must not reference transient eval/internal dynamic assemblies.");
        }

        [Test]
        public void ModernPersistedAotUsesSameRuntimeTargetFrameworkPolicy()
        {
            using AotSample sample = AotSample.Create();
            GenContext context = CompileSampleWithExplicitContext(sample);

            Assert.That(context.UsesSameRuntimePersistedCoreAssembly, Is.True,
                "The first modern persisted AOT path intentionally targets the executing runtime.");
            Assert.That(context.PersistedCoreAssembly, Is.SameAs(typeof(object).Assembly));
        }

        [Test]
        public void ModernPersistedAotCanSelectExplicitReferenceAssemblyTargetFramework()
        {
            string targetFramework = CurrentTestTargetFramework();
            string referenceAssemblyDirectory = FindReferenceAssemblyDirectory(targetFramework);
            if (referenceAssemblyDirectory is null)
                Assert.Ignore($"No Microsoft.NETCore.App.Ref reference assemblies are installed for {targetFramework}.");

            using AotSample sample = AotSample.Create();
            GenContext context = CompileSampleWithExplicitContext(
                sample,
                targetFramework: targetFramework,
                referenceAssemblyPath: referenceAssemblyDirectory);

            Assert.That(context.UsesSameRuntimePersistedCoreAssembly, Is.False,
                "Explicit persisted AOT target selection should use reference assemblies, not the compiler runtime core assembly.");
            Assert.That(context.PersistedCoreAssembly.GetName().Name, Is.EqualTo("System.Runtime"));
            Assert.That(context.PersistedTargetFramework, Is.EqualTo(targetFramework));
            Assert.That(context.PersistedReferenceAssemblyDirectory, Is.EqualTo(Path.GetFullPath(referenceAssemblyDirectory)));
            Assert.That(VarValue(sample, "invoked"), Is.EqualTo(42));

            SaveExplicitContext(context);

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            string[] referenceNames = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
            Assert.That(referenceNames, Does.Contain("System.Runtime"),
                "Explicit target selection should bind core framework references through the selected reference assemblies.");
            Assert.That(referenceNames, Does.Not.Contain("System.Private.CoreLib"),
                "Explicit target selection must not leak compiler runtime implementation assemblies into saved metadata."
                + Environment.NewLine
                + DumpMetadataReferences(sample.AssemblyPath, "System.Private.CoreLib"));
            Assert.That(assembly.GetReferencedAssemblies().Any(IsEvalOrInternalDynamicReference), Is.False,
                "Explicit target selection should still avoid transient eval/internal dynamic assembly references.");
        }

        [Test]
        public void ModernPersistedAotEmitsVerifiedPortableDebugSymbols()
        {
            using AotSample sample = AotSample.Create(DebugSymbolsBody);
            GenContext context = CompileSampleWithExplicitContext(sample);

#if !DEBUG
            Assert.That(context.IsDebuggable, Is.False);
            Assert.Ignore("Persisted AOT debug symbol emission is enabled for Debug builds only.");
#else
            Assert.That(context.IsDebuggable, Is.True,
                "Debug builds should emit persisted AOT debug metadata after the portable PDB path is verified.");
            Assert.That(context.DocWriter, Is.Not.Null,
                "Persisted AOT should define a symbol document before sequence point emission.");

            SaveExplicitContext(context);

            using (FileStream stream = File.OpenRead(sample.AssemblyPath))
            using (PEReader peReader = new(stream))
            {
                var debugDirectory = peReader.ReadDebugDirectory();

                Assert.That(peReader.PEHeaders.IsDll, Is.True,
                    "The manual persisted save path should still produce a loadable DLL image.");
                Assert.That(debugDirectory.Any(entry => entry.Type == DebugDirectoryEntryType.Reproducible), Is.True,
                    "Persisted debug output should mark the PE as reproducible.");

                DebugDirectoryEntry codeViewEntry = debugDirectory.Single(entry =>
                    entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView);
                CodeViewDebugDirectoryData codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);

                Assert.That(codeView.Path, Is.EqualTo(Path.ChangeExtension(Path.GetFileName(sample.AssemblyPath), ".pdb")));
                Assert.That(codeView.Age, Is.EqualTo(1));

                DebugDirectoryEntry checksumEntry = debugDirectory.Single(entry =>
                    entry.Type == DebugDirectoryEntryType.PdbChecksum);
                PdbChecksumDebugDirectoryData checksum = peReader.ReadPdbChecksumDebugDirectoryData(checksumEntry);

                Assert.That(checksum.AlgorithmName, Is.EqualTo("SHA256"));
                Assert.That(checksum.Checksum.Length, Is.EqualTo(32));

                DebugDirectoryEntry embeddedPdbEntry = debugDirectory.Single(entry =>
                    entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
                using MetadataReaderProvider pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry);
                MetadataReader pdbReader = pdbProvider.GetMetadataReader();

                string[] documentNames = pdbReader.Documents
                    .Select(handle => pdbReader.GetString(pdbReader.GetDocument(handle).Name))
                    .ToArray();
                SequencePoint[] sequencePoints = pdbReader.MethodDebugInformation
                    .SelectMany(handle => pdbReader.GetMethodDebugInformation(handle).GetSequencePoints())
                    .Where(point => !point.IsHidden)
                    .ToArray();

                Assert.That(documentNames, Does.Contain(sample.SourceFileName),
                    "Persisted sequence points should map back to the compiled Clojure source file.");
                Assert.That(sequencePoints, Is.Not.Empty,
                    "Persisted portable PDBs should contain emitted Clojure sequence points.");
                Assert.That(sequencePoints.Any(point => point.StartLine <= 4 && point.EndLine >= 4), Is.True,
                    "The generated function body should retain a source span covering (inc answer).");
            }

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            Assert.That(assembly.GetCustomAttribute<DebuggableAttribute>(), Is.Not.Null);
#endif
        }

        [TestCaseSource(nameof(UnsupportedGeneratedFormCases))]
        public void ModernPersistedAotRejectsFirstPassGeneratedForms(UnsupportedGeneratedFormCase testCase)
        {
            using AotSample sample = AotSample.CreateFromSource(testCase.SourceFactory);

            Exception ex = Assert.Catch<Exception>(() => CompileSample(sample));
            InvalidOperationException unsupported = FindException<InvalidOperationException>(ex);

            Assert.That(unsupported, Is.Not.Null, ex.ToString());
            Assert.That(unsupported.Message, Does.Contain(testCase.FeatureName));
            Assert.That(unsupported.Message, Does.Contain("persisted AOT"));
            Assert.That(File.Exists(sample.AssemblyPath), Is.False,
                "Rejected generated forms should not leave a persisted namespace DLL.");
        }

        [Test]
        public void MinimalNamespaceAotRecordsGeneratedArtifactIdentities()
        {
            using AotSample sample = AotSample.Create();
            GenContext context = CompileSampleWithExplicitContext(sample);

            GeneratedTypeRecord initType = context.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.InitTypeName);
            GeneratedTypeRecord fnType = context.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.NamespaceName + "$inc_answer");

            Assert.That(initType, Is.Not.Null, "Namespace init type should be registered.");
            Assert.That(fnType, Is.Not.Null, "Generated defn function type should be registered.");
            Assert.That(initType.Members.Values.Any(IsPersistedInitializeMethod), Is.True,
                "Namespace init type should record Initialize().");
            Assert.That(fnType.Members.Values.Any(IsPersistedInvokeStaticMethod), Is.True,
                "Generated defn function type should record invokeStatic().");
        }

        [Test]
        public void MinimalNamespaceAotRecordsConstantsVarsKeywordsConstructorsFieldsAndHelpers()
        {
            using AotSample sample = AotSample.Create();
            GenContext context = CompileSampleWithExplicitContext(sample);

            GeneratedTypeRecord initType = context.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.InitTypeName);
            GeneratedTypeRecord fnType = context.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.NamespaceName + "$inc_answer");

            Assert.That(initType, Is.Not.Null, "Namespace init type should be registered.");
            Assert.That(fnType, Is.Not.Null, "Generated defn function type should be registered.");

            FieldInfo[] initConstants = PersistedConstantFields(initType);
            Assert.That(HasPersistedMember(initType, GeneratedMemberKind.Method, "Initialize"), Is.True,
                "Namespace init type should record Initialize().");
            Assert.That(HasPersistedMember(initType, GeneratedMemberKind.Method, ObjExpr.StaticCtorHelperName + "_constants"), Is.True,
                "Namespace init type should record the constants helper method.");
            Assert.That(HasPersistedMember(initType, GeneratedMemberKind.StaticConstructor, ".cctor"), Is.True,
                "Namespace init type should record its static constructor.");
            Assert.That(initConstants, Is.Not.Empty,
                "Namespace init type should record emitted constant fields.");
            Assert.That(initConstants.Any(field => field.FieldType == typeof(Var)), Is.True,
                "Namespace init constants should include emitted Var constants.");
            Assert.That(initConstants.Any(field => field.FieldType == typeof(Keyword)), Is.True,
                "Namespace init constants should include emitted Keyword constants.");

            FieldInfo[] fnConstants = PersistedConstantFields(fnType);
            Assert.That(HasPersistedMember(fnType, GeneratedMemberKind.Constructor, ".ctor"), Is.True,
                "Generated function type should record its public constructor.");
            Assert.That(HasPersistedMember(fnType, GeneratedMemberKind.StaticConstructor, ".cctor"), Is.True,
                "Generated function type should record its static constructor.");
            Assert.That(HasPersistedMember(fnType, GeneratedMemberKind.Method, "invokeStatic"), Is.True,
                "Generated function type should record invokeStatic().");
            Assert.That(HasPersistedMember(fnType, GeneratedMemberKind.Method, "invoke"), Is.True,
                "Generated function type should record invoke().");
            Assert.That(HasPersistedMember(fnType, GeneratedMemberKind.Method, "HasArity"), Is.True,
                "Generated function type should record HasArity().");
            Assert.That(fnConstants.Any(field => field.FieldType == typeof(Var)), Is.True,
                "Generated function type should record Var-backed constant fields.");
        }

        [Test]
        public void MinimalNamespaceAotCarriesExplicitEvalAndPersistedGenerationContexts()
        {
            using AotSample sample = AotSample.Create();
            GenContext persistedContext = CompileSampleWithExplicitContext(sample);
            GenerationContextPair generationContexts = persistedContext.GenerationContexts;

            Assert.That(generationContexts, Is.Not.Null, "AOT compile context should expose its paired generation contexts.");
            Assert.That(generationContexts.PersistedContext, Is.SameAs(persistedContext));
            Assert.That(generationContexts.EvalContext.ArtifactBackend, Is.EqualTo(GeneratedArtifactBackend.Eval));
            Assert.That(generationContexts.EvalContext.CanRunNow, Is.True);
            Assert.That(persistedContext.CanPersist, Is.True);
            Assert.That(generationContexts.GeneratedArtifacts, Is.SameAs(persistedContext.GeneratedArtifacts));
            Assert.That(generationContexts.EvalContext.GeneratedArtifacts, Is.SameAs(persistedContext.GeneratedArtifacts));

            GeneratedTypeRecord fnType = persistedContext.GeneratedArtifacts.Types.SingleOrDefault(
                t => t.GetRuntimeName(GeneratedArtifactBackend.Persisted) == sample.NamespaceName + "$inc_answer");

            Assert.That(fnType, Is.Not.Null, "Generated defn function type should be registered.");
            Assert.That(fnType.GetCreatedType(GeneratedArtifactBackend.Persisted), Is.Not.Null,
                "Persisted pass should record the persisted function type.");
            Assert.That(fnType.GetCreatedType(GeneratedArtifactBackend.Eval), Is.Not.Null,
                "Separate eval pass should record the runnable eval function type on the same logical artifact.");
            Assert.That(fnType.Members.Values.Any(IsEvalInvokeStaticMethod), Is.True,
                "Separate eval pass should record eval-side invokeStatic().");
        }

        [Test]
        public void MinimalNamespaceAotPairsGeneratedFunctionClassIdentitiesWithBackendLocalNames()
        {
            using AotSample sample = AotSample.Create();
            GenContext persistedContext = CompileSampleWithExplicitContext(sample);

            GeneratedTypeRecord helperType = persistedContext.GeneratedArtifacts.Types.SingleOrDefault(type =>
                IsGeneratedHelperRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Persisted))
                && IsGeneratedHelperRuntimeName(type.GetRuntimeName(GeneratedArtifactBackend.Eval)));

            Assert.That(helperType, Is.Not.Null,
                "Named function literals should share one logical artifact across persisted and eval passes."
                + Environment.NewLine
                + DumpGeneratedTypes(persistedContext));
            Assert.That(helperType.GetRuntimeName(GeneratedArtifactBackend.Persisted),
                Is.Not.EqualTo(helperType.GetRuntimeName(GeneratedArtifactBackend.Eval)),
                "Runtime names can keep backend-local RT.nextID suffixes.");
            Assert.That(Regex.IsMatch(helperType.Id.LogicalName, @"__\d+(?=__|\$|$)"), Is.False,
                "Logical generated type ids should not include backend-local RT.nextID suffixes.");
            Assert.That(helperType.GetTypeBuilder(GeneratedArtifactBackend.Persisted), Is.Not.Null);
            Assert.That(helperType.GetTypeBuilder(GeneratedArtifactBackend.Eval), Is.Not.Null);
            Assert.That(helperType.GetCreatedType(GeneratedArtifactBackend.Persisted), Is.Not.Null);
            Assert.That(helperType.GetCreatedType(GeneratedArtifactBackend.Eval), Is.Not.Null);
        }

        [Test]
        public void ProgressiveMacroAotPreservesCompileTimeMacroAndLaterFormDependencies()
        {
            using AotSample sample = AotSample.Create(ProgressiveMacroBody);
            CompileSample(sample);

            Assert.That(VarValue(sample, "macro-produced"), Is.EqualTo(Keyword.intern(null, "macro-ok")));
            Assert.That(VarValue(sample, "later-form-sees-macro-produced"), Is.EqualTo(":macro-ok"));
            Assert.That(VarValue(sample, "later-call"), Is.EqualTo(":macro-ok|:macro-ok"));
            Assert.That(VarValue(sample, "after-macro"), Is.EqualTo(Keyword.intern(null, "loaded")));

            Assembly assembly = Assembly.LoadFrom(sample.AssemblyPath);
            string[] typeNames = assembly.GetTypes().Select(t => t.FullName).ToArray();
            Assert.That(typeNames, Does.Contain(sample.NamespaceName + "$defprogressive"),
                "Persisted assembly should contain the generated macro function class.");
        }

        [Test]
        public async Task ProgressiveMacroAotLoadsWithoutSourceInFreshProcess()
        {
            using AotSample sample = AotSample.Create(ProgressiveMacroBody);
            CompileSample(sample);
            sample.DeleteSourceTree();

            string mainAssemblyPath = GetBuiltProjectAssemblyPath("Clojure.Main", "Clojure.Main.dll");
            Assert.That(File.Exists(mainAssemblyPath), Is.True,
                $"Build output for Clojure.Main was not found at {mainAssemblyPath}.");

            string script =
                $"(require '{sample.NamespaceName}) " +
                $"(println @#'{sample.NamespaceName}/macro-produced) " +
                $"(println @#'{sample.NamespaceName}/later-form-sees-macro-produced) " +
                $"(println @#'{sample.NamespaceName}/later-call) " +
                $"(println @#'{sample.NamespaceName}/after-macro)";

            ProcessResult result = await RunProcessAsync("dotnet", sample.CompilePath, startInfo =>
            {
                startInfo.ArgumentList.Add(mainAssemblyPath);
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(script);
                startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                startInfo.Environment[RT.ClojureLoadPathString] = sample.CompilePath;
            });

            Assert.That(result.ExitCode, Is.EqualTo(0), result.ToFailureMessage("progressive macro source-free load"));

            string[] stdoutLines = result.StandardOutput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(stdoutLines, Is.EqualTo(new[] { ":macro-ok", ":macro-ok", ":macro-ok|:macro-ok", ":loaded" }),
                result.ToFailureMessage("progressive macro source-free load"));
        }

        [Test]
        public void RuntimeNamespaceTrancheAotProducesPersistedAssemblies()
        {
            using AotCompileOutput output = AotCompileOutput.Create();
            CompileRuntimeNamespaces(RuntimeNamespaceTranche, output.CompilePath);

            foreach (string namespaceName in RuntimeNamespaceTranche)
            {
                string assemblyPath = output.GetAssemblyPath(namespaceName);
                Assert.That(File.Exists(assemblyPath), Is.True,
                    $"{namespaceName} should produce a persisted namespace DLL.");

                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                AssemblyName[] references = assembly.GetReferencedAssemblies();
                Type initType = assembly.GetType(GetInitTypeName(namespaceName));

                Assert.That(references.Any(IsEvalOrInternalDynamicReference), Is.False,
                    $"{namespaceName} must not reference transient eval/internal dynamic assemblies.");
                Assert.That(initType, Is.Not.Null,
                    $"{namespaceName} should contain the namespace initializer type.");
                Assert.That(initType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static), Is.Not.Null,
                    $"{namespaceName} initializer should expose public static Initialize().");
            }
        }

        [Test]
        public async Task RuntimeNamespaceTrancheAotLoadsInFreshProcess()
        {
            using AotCompileOutput output = AotCompileOutput.Create();
            CompileRuntimeNamespaces(RuntimeNamespaceTranche, output.CompilePath);

            string mainAssemblyPath = GetBuiltProjectAssemblyPath("Clojure.Main", "Clojure.Main.dll");
            Assert.That(File.Exists(mainAssemblyPath), Is.True,
                $"Build output for Clojure.Main was not found at {mainAssemblyPath}.");

            string script =
                "(require 'clojure.walk 'clojure.template 'clojure.set 'clojure.string 'clojure.data) " +
                "(println (clojure.set/subset? #{:a} #{:a :b})) " +
                "(println (pr-str (clojure.walk/postwalk-replace {:a :b} [:a {:a :a}]))) " +
                "(println (pr-str (macroexpand '(clojure.template/do-template [x] x :ok)))) " +
                "(println (clojure.string/replace \"a1b2\" #\"\\d\" (fn [m] (str \"[\" m \"]\")))) " +
                "(println (= (clojure.data/diff 1 2) [1 2 nil]))";

            ProcessResult result = await RunProcessAsync("dotnet", output.CompilePath, startInfo =>
            {
                startInfo.ArgumentList.Add(mainAssemblyPath);
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(script);
                startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                startInfo.Environment[RT.ClojureLoadPathString] = output.CompilePath;
            });

            Assert.That(result.ExitCode, Is.EqualTo(0), result.ToFailureMessage("runtime namespace tranche load"));

            string[] stdoutLines = result.StandardOutput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(stdoutLines, Is.EqualTo(new[] { "true", "[:b {:b :b}]", "(do :ok)", "a[1]b[2]", "true" }),
                result.ToFailureMessage("runtime namespace tranche load"));
        }

        [Test]
        public async Task MinimalNamespaceAotLoadsWithoutSourceInFreshProcess()
        {
            using AotSample sample = AotSample.Create();
            CompileSample(sample);
            sample.DeleteSourceTree();

            string mainAssemblyPath = GetBuiltProjectAssemblyPath("Clojure.Main", "Clojure.Main.dll");
            Assert.That(File.Exists(mainAssemblyPath), Is.True,
                $"Build output for Clojure.Main was not found at {mainAssemblyPath}.");

            string script =
                $"(require '{sample.NamespaceName}) " +
                $"(println @#'{sample.NamespaceName}/invoked) " +
                $"(println @#'{sample.NamespaceName}/after-let)";

            ProcessResult result = await RunProcessAsync("dotnet", sample.CompilePath, startInfo =>
            {
                startInfo.ArgumentList.Add(mainAssemblyPath);
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(script);
                startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
                startInfo.Environment[RT.ClojureLoadPathString] = sample.CompilePath;
            });

            Assert.That(result.ExitCode, Is.EqualTo(0), result.ToFailureMessage("source-free load"));

            string[] stdoutLines = result.StandardOutput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(stdoutLines, Is.EqualTo(new[] { "42", ":loaded" }), result.ToFailureMessage("source-free load"));
        }

        [Test]
        public async Task MinimalNamespaceAotPassesIlVerifyWhenConfigured()
        {
            string ilVerifyPath = Environment.GetEnvironmentVariable("CLOJURE_AOT_ILVERIFY");
            if (string.IsNullOrWhiteSpace(ilVerifyPath))
                Assert.Ignore("Set CLOJURE_AOT_ILVERIFY to an ilverify executable path to enable this gate.");

            Assert.That(File.Exists(ilVerifyPath), Is.True,
                $"CLOJURE_AOT_ILVERIFY points to a missing file: {ilVerifyPath}");

            using AotSample sample = AotSample.Create();
            CompileSample(sample);

            string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            string resolverDir = TestContext.CurrentContext.TestDirectory;

            ProcessResult result = await RunProcessAsync(ilVerifyPath, GetRepoRoot(), startInfo =>
            {
                startInfo.ArgumentList.Add(sample.AssemblyPath);
                startInfo.ArgumentList.Add("-r");
                startInfo.ArgumentList.Add(Path.Combine(resolverDir, "*.dll"));
                startInfo.ArgumentList.Add("-r");
                startInfo.ArgumentList.Add(Path.Combine(runtimeDir, "*.dll"));
            });

            Assert.That(result.ExitCode, Is.EqualTo(0), result.ToFailureMessage("dotnet-ilverify"));
            Assert.That(result.StandardOutput, Does.Contain("Verified"), result.ToFailureMessage("dotnet-ilverify"));
        }

        private static void CompileSample(AotSample sample, bool directLinking = false)
        {
            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sample.SourceRoot
                : sample.SourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = CompilerOptions(directLinking);

            try
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, testLoadPath);
                Var.pushThreadBindings(RT.map(
                    Compiler.CompilePathVar, sample.CompilePath,
                    compilerOptionsVar, compilerOptions));

                try
                {
                    Compiler.CompileVar.invoke(Symbol.intern(sample.NamespaceName));
                }
                finally
                {
                    Var.popThreadBindings();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, previousLoadPath);
            }
        }

        private static GenContext CompileSampleWithExplicitContext(
            AotSample sample,
            bool directLinking = false,
            string targetFramework = null,
            string referenceAssemblyPath = null)
        {
            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sample.SourceRoot
                : sample.SourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = CompilerOptions(directLinking, targetFramework, referenceAssemblyPath);

            try
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, testLoadPath);
                Var.pushThreadBindings(RT.map(
                    Compiler.CompilePathVar, sample.CompilePath,
                    compilerOptionsVar, compilerOptions));

                try
                {
                    GenContext context = GenContext.CreateWithExternalAssembly(
                        sample.SourceFileName,
                        sample.RelativePath,
                        ".dll",
                        true);

                    using TextReader reader = File.OpenText(sample.SourcePath);
                    Compiler.Compile(
                        context,
                        reader,
                        Path.GetDirectoryName(sample.SourcePath),
                        sample.SourceFileName,
                        sample.RelativePath);
                    return context;
                }
                finally
                {
                    Var.popThreadBindings();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, previousLoadPath);
            }
        }

        private static void CompileRuntimeNamespaces(string[] namespaceNames, string compilePath)
        {
            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string sourceRoot = Path.Combine(GetRepoRoot(), "Clojure.Source");
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sourceRoot
                : sourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = CompilerOptions(directLinking: false);

            try
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, testLoadPath);
                Var.pushThreadBindings(RT.map(
                    Compiler.CompilePathVar, compilePath,
                    compilerOptionsVar, compilerOptions));

                try
                {
                    foreach (string namespaceName in namespaceNames)
                        Compiler.CompileVar.invoke(Symbol.intern(namespaceName));
                }
                finally
                {
                    Var.popThreadBindings();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, previousLoadPath);
            }
        }

        private static object CompilerOptions(
            bool directLinking,
            string targetFramework = null,
            string referenceAssemblyPath = null)
        {
            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = RT.assoc(
                compilerOptionsVar.deref(),
                Keyword.intern(null, "direct-linking"),
                directLinking);

            if (!string.IsNullOrWhiteSpace(targetFramework))
            {
                compilerOptions = RT.assoc(
                    compilerOptions,
                    Keyword.intern(null, "aot-target-framework"),
                    targetFramework);
            }

            if (!string.IsNullOrWhiteSpace(referenceAssemblyPath))
            {
                compilerOptions = RT.assoc(
                    compilerOptions,
                    Keyword.intern(null, "aot-reference-assembly-path"),
                    referenceAssemblyPath);
            }

            return compilerOptions;
        }

        private static string CurrentTestTargetFramework()
        {
            string targetFramework = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Name;
            Match match = Regex.Match(targetFramework, @"^net(?<version>\d+\.\d+)");
            if (match.Success)
                return "net" + match.Groups["version"].Value;

            if (!string.IsNullOrWhiteSpace(AppContext.TargetFrameworkName))
            {
                FrameworkName frameworkName = new(AppContext.TargetFrameworkName);
                if (frameworkName.Identifier.Equals(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
                    return "net" + frameworkName.Version.Major + "." + frameworkName.Version.Minor;
            }

            throw new InvalidOperationException("Could not determine the current test target framework.");
        }

        private static string FindReferenceAssemblyDirectory(string targetFramework)
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
                    string candidate = Path.Combine(versionDirectory, "ref", targetFramework);
                    if (!File.Exists(Path.Combine(candidate, "System.Runtime.dll")))
                        continue;

                    string version = new DirectoryInfo(versionDirectory).Name;
                    if (bestDirectory is null || ComparePackVersions(version, bestVersion) > 0)
                    {
                        bestDirectory = candidate;
                        bestVersion = version;
                    }
                }
            }

            return bestDirectory;
        }

        private static string[] DotNetRootCandidates()
        {
            string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            string runtimeDotNetRoot = null;
            if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                DirectoryInfo runtimeVersionDirectory = new(runtimeDirectory);
                runtimeDotNetRoot = runtimeVersionDirectory.Parent?.Parent?.Parent?.FullName;
            }

            return new[]
            {
                runtimeDotNetRoot,
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Environment.GetEnvironmentVariable("DOTNET_ROOT_ARM64"),
                Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
                Environment.GetEnvironmentVariable("DOTNET_ROOT_X86")
            }.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
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

        private static bool IsPersistedInitializeMethod(GeneratedMemberRecord member)
        {
            return member.Id.Kind == GeneratedMemberKind.Method
                && member.Id.LogicalName == "Initialize"
                && member.PersistedMember is MethodInfo;
        }

        private static bool IsPersistedInvokeStaticMethod(GeneratedMemberRecord member)
        {
            return member.Id.Kind == GeneratedMemberKind.Method
                && member.Id.LogicalName == "invokeStatic"
                && member.PersistedMember is MethodInfo;
        }

        private static bool IsEvalInvokeStaticMethod(GeneratedMemberRecord member)
        {
            return member.Id.Kind == GeneratedMemberKind.Method
                && member.Id.LogicalName == "invokeStatic"
                && member.EvalMember is MethodInfo;
        }

        private static bool HasPersistedMember(
            GeneratedTypeRecord type,
            GeneratedMemberKind kind,
            string logicalName)
        {
            return type.Members.Values.Any(member =>
                member.Id.Kind == kind
                && member.Id.LogicalName == logicalName
                && member.PersistedMember is not null);
        }

        private static FieldInfo[] PersistedConstantFields(GeneratedTypeRecord type)
        {
            return type.Members.Values
                .Where(member =>
                    member.Id.Kind == GeneratedMemberKind.Field
                    && member.Id.LogicalName.StartsWith(ObjExpr.ConstPrefix, StringComparison.Ordinal)
                    && member.PersistedMember is FieldInfo)
                .Select(member => (FieldInfo)member.PersistedMember)
                .ToArray();
        }

        private static void SaveExplicitContext(GenContext context)
        {
            typeof(GenContext)
                .GetMethod("SaveAssembly", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(context, null);
        }

        private static object VarValue(AotSample sample, string varName)
        {
            Var var = Var.find(Symbol.intern(sample.NamespaceName, varName));
            Assert.That(var, Is.Not.Null, $"Compiled namespace should define {varName}.");
            return var.deref();
        }

        private static TException FindException<TException>(Exception ex)
            where TException : Exception
        {
            while (ex is not null)
            {
                if (ex is TException matching)
                    return matching;

                ex = ex.InnerException;
            }

            return null;
        }

        private static bool IsGeneratedHelperRuntimeName(string name)
        {
            return name is not null && Regex.IsMatch(name, @"\$helper__\d+(?=__|\$|$)");
        }

        private static bool IsReifyRuntimeName(string name)
        {
            return name is not null && Regex.IsMatch(name, @"\$reify__\d+(?=__|\$|$)");
        }

        private static bool IsDynamicHostInteropHelper(string name)
        {
            return name is not null
                && (name.Contains("__dynInitHelper", StringComparison.Ordinal)
                    || name.StartsWith("__InternalDynamicExpressionInits_", StringComparison.Ordinal));
        }

        private static string DumpGeneratedTypes(GenContext context)
        {
            return string.Join(
                Environment.NewLine,
                context.GeneratedArtifacts.Types.Select(type =>
                    $"{type.Id.LogicalName} | persisted={type.GetRuntimeName(GeneratedArtifactBackend.Persisted) ?? "<none>"} | eval={type.GetRuntimeName(GeneratedArtifactBackend.Eval) ?? "<none>"}"));
        }

        private static bool IsEvalOrInternalDynamicReference(AssemblyName reference)
        {
            string name = reference.Name ?? string.Empty;
            return name.Equals("eval", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("eval", StringComparison.OrdinalIgnoreCase)
                || name.Contains("InternalDynamic", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DynamicMethods", StringComparison.OrdinalIgnoreCase);
        }

        private static string DumpMetadataReferences(string assemblyPath, string assemblyName)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader peReader = new(stream);
            MetadataReader reader = peReader.GetMetadataReader();

            var matchingAssemblyReferences = reader.AssemblyReferences
                .Where(handle => reader.GetString(reader.GetAssemblyReference(handle).Name) == assemblyName)
                .ToArray();

            if (matchingAssemblyReferences.Length == 0)
                return assemblyName + " metadata references: <none>";

            string[] typeReferences = reader.TypeReferences
                .Select(handle => reader.GetTypeReference(handle))
                .Where(typeReference =>
                    typeReference.ResolutionScope.Kind == HandleKind.AssemblyReference
                    && matchingAssemblyReferences.Contains((AssemblyReferenceHandle)typeReference.ResolutionScope))
                .Select(typeReference => reader.GetString(typeReference.Namespace) + "." + reader.GetString(typeReference.Name))
                .Distinct()
                .OrderBy(name => name)
                .ToArray();

            string[] memberReferences = reader.MemberReferences
                .Select(handle => reader.GetMemberReference(handle))
                .Where(memberReference =>
                    memberReference.Parent.Kind == HandleKind.TypeReference
                    && reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent).ResolutionScope.Kind == HandleKind.AssemblyReference
                    && matchingAssemblyReferences.Contains((AssemblyReferenceHandle)reader.GetTypeReference((TypeReferenceHandle)memberReference.Parent).ResolutionScope))
                .Select(memberReference => reader.GetString(memberReference.Name))
                .Distinct()
                .OrderBy(name => name)
                .ToArray();

            return assemblyName
                + " type refs: "
                + (typeReferences.Length == 0 ? "<none>" : string.Join(", ", typeReferences))
                + Environment.NewLine
                + assemblyName
                + " member refs: "
                + (memberReferences.Length == 0 ? "<none>" : string.Join(", ", memberReferences));
        }

        private static string GetInitTypeName(string namespaceName)
        {
            return "__Init__$" + namespaceName.Replace(".", "$");
        }

        private static string GetBuiltProjectAssemblyPath(string projectName, string assemblyFileName)
        {
            string testDirectory = TestContext.CurrentContext.TestDirectory;
            string targetFramework = new DirectoryInfo(testDirectory).Name;
            string configuration = new DirectoryInfo(testDirectory).Parent?.Name ?? "Debug";
            return Path.Combine(GetRepoRoot(), projectName, "bin", configuration, targetFramework, assemblyFileName);
        }

        private static string GetRepoRoot()
        {
            DirectoryInfo directory = new(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Clojure.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate Clojure.sln from the test output directory.");
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string fileName,
            string workingDirectory,
            Action<ProcessStartInfo> configure)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            configure(startInfo);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task waitTask = process.WaitForExitAsync();

            if (await Task.WhenAny(waitTask, Task.Delay(ProcessTimeoutMilliseconds)) != waitTask)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Assert.Fail($"{fileName} did not exit within {ProcessTimeoutMilliseconds} ms.");
            }

            await waitTask;
            return new ProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }

        private sealed class AotSample : IDisposable
        {
            private AotSample(
                string workDir,
                string sourceRoot,
                string compilePath,
                string namespaceName,
                string assemblyPath,
                string sourcePath,
                string sourceFileName,
                string relativePath)
            {
                WorkDir = workDir;
                SourceRoot = sourceRoot;
                CompilePath = compilePath;
                NamespaceName = namespaceName;
                AssemblyPath = assemblyPath;
                SourcePath = sourcePath;
                SourceFileName = sourceFileName;
                RelativePath = relativePath;
            }

            public string WorkDir { get; }
            public string SourceRoot { get; }
            public string CompilePath { get; }
            public string NamespaceName { get; }
            public string AssemblyPath { get; }
            public string SourcePath { get; }
            public string SourceFileName { get; }
            public string RelativePath { get; }
            public string InitTypeName => GetInitTypeName(NamespaceName);

            public static AotSample Create(string body = SampleBody)
            {
                return CreateFromSource(namespaceName => $"(ns {namespaceName})\n{body}");
            }

            public static AotSample CreateFromSource(Func<string, string> sourceFactory)
            {
                string workDir = Path.Combine(Path.GetTempPath(), "clj-aot-test-" + Guid.NewGuid().ToString("N"));
                string sourceRoot = Path.Combine(workDir, "src");
                string sourceDir = Path.Combine(sourceRoot, "aot");
                string compilePath = Path.Combine(workDir, "out");
                string leafName = "smoke" + Guid.NewGuid().ToString("N");
                string namespaceName = "aot." + leafName;
                string relativePath = Path.Combine("aot", leafName + ".clj");
                string sourcePath = Path.Combine(sourceDir, leafName + ".clj");
                string sourceFileName = leafName + ".clj";
                string assemblyPath = Path.Combine(compilePath, namespaceName + ".clj.dll");

                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(compilePath);
                File.WriteAllText(sourcePath, sourceFactory(namespaceName));

                return new AotSample(
                    workDir,
                    sourceRoot,
                    compilePath,
                    namespaceName,
                    assemblyPath,
                    sourcePath,
                    sourceFileName,
                    relativePath);
            }

            public void DeleteSourceTree()
            {
                if (Directory.Exists(SourceRoot))
                    Directory.Delete(SourceRoot, true);
            }

            public void Dispose()
            {
                try { Directory.Delete(WorkDir, true); } catch { }
            }
        }

        private sealed class AotCompileOutput : IDisposable
        {
            private AotCompileOutput(string workDir, string compilePath)
            {
                WorkDir = workDir;
                CompilePath = compilePath;
            }

            public string WorkDir { get; }
            public string CompilePath { get; }

            public static AotCompileOutput Create()
            {
                string workDir = Path.Combine(Path.GetTempPath(), "clj-aot-runtime-test-" + Guid.NewGuid().ToString("N"));
                string compilePath = Path.Combine(workDir, "out");
                Directory.CreateDirectory(compilePath);
                return new AotCompileOutput(workDir, compilePath);
            }

            public string GetAssemblyPath(string namespaceName)
            {
                return Path.Combine(CompilePath, namespaceName + ".clj.dll");
            }

            public void Dispose()
            {
                try { Directory.Delete(WorkDir, true); } catch { }
            }
        }

        private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
        {
            public string ToFailureMessage(string operation)
                => $"{operation} failed with exit code {ExitCode}."
                   + $"{Environment.NewLine}stdout:{Environment.NewLine}{StandardOutput}"
                   + $"{Environment.NewLine}stderr:{Environment.NewLine}{StandardError}";
        }

        public sealed record UnsupportedGeneratedFormCase(string FeatureName, Func<string, string> SourceFactory)
        {
            public override string ToString() => FeatureName;
        }
    }
}

#endif
