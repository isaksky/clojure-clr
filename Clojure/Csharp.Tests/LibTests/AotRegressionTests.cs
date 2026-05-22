#if NET9_0_OR_GREATER

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
";

        private static readonly UnsupportedGeneratedFormCase[] UnsupportedGeneratedFormCases =
        [
            new(
                "deftype*",
                ns => $@"(ns {ns})
(deftype AotBox [x])
(def after-generated-form :unreachable)"),
            new(
                "reify*",
                ns => $@"(ns {ns})
(def disposable (reify System.IDisposable
                  (Dispose [this] nil)))"),
            new(
                "gen-class",
                ns => $@"(ns {ns})
(gen-class :name {ns}.GeneratedClass :load-impl-ns false)"),
            new(
                "proxy",
                ns => $@"(ns {ns})
(def writer (proxy [System.IO.StringWriter] []))"),
            new(
                "gen-interface",
                ns => $@"(ns {ns})
(gen-interface :name {ns}.GeneratedInterface :methods [[m [] Object]])"),
            new(
                "gen-delegate",
                ns => $@"(ns {ns})
(def starter (gen-delegate System.Threading.ThreadStart [] nil))")
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
        public void ModernPersistedAotRejectsDynamicHostInteropCallSites()
        {
            using AotSample sample = AotSample.Create(DynamicHostInteropBody);

            Compiler.CompilerException ex = Assert.Throws<Compiler.CompilerException>(() => CompileSample(sample));

            Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(ex.InnerException.Message, Does.Contain("Dynamic host interop is not supported"));
            Assert.That(File.Exists(sample.AssemblyPath), Is.False,
                "Rejected dynamic host interop forms should not leave a persisted namespace DLL.");
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
            object compilerOptions = RT.assoc(
                compilerOptionsVar.deref(),
                Keyword.intern(null, "direct-linking"),
                directLinking);

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

        private static GenContext CompileSampleWithExplicitContext(AotSample sample, bool directLinking = false)
        {
            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sample.SourceRoot
                : sample.SourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = RT.assoc(
                compilerOptionsVar.deref(),
                Keyword.intern(null, "direct-linking"),
                directLinking);

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
            public string InitTypeName => "__Init__$" + NamespaceName.Replace(".", "$");

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
