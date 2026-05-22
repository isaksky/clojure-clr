#if NET9_0_OR_GREATER

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using clojure.lang;
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

(let [x 5]
  (+ x invoked))

(def after-let :loaded)
";

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

        private static void CompileSample(AotSample sample)
        {
            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sample.SourceRoot
                : sample.SourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = RT.assoc(
                compilerOptionsVar.deref(),
                Keyword.intern(null, "direct-linking"),
                false);

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
                string assemblyPath)
            {
                WorkDir = workDir;
                SourceRoot = sourceRoot;
                CompilePath = compilePath;
                NamespaceName = namespaceName;
                AssemblyPath = assemblyPath;
            }

            public string WorkDir { get; }
            public string SourceRoot { get; }
            public string CompilePath { get; }
            public string NamespaceName { get; }
            public string AssemblyPath { get; }
            public string InitTypeName => "__Init__$" + NamespaceName.Replace(".", "$");

            public static AotSample Create()
            {
                string workDir = Path.Combine(Path.GetTempPath(), "clj-aot-test-" + Guid.NewGuid().ToString("N"));
                string sourceRoot = Path.Combine(workDir, "src");
                string sourceDir = Path.Combine(sourceRoot, "aot");
                string compilePath = Path.Combine(workDir, "out");
                string leafName = "smoke" + Guid.NewGuid().ToString("N");
                string namespaceName = "aot." + leafName;
                string sourcePath = Path.Combine(sourceDir, leafName + ".clj");
                string assemblyPath = Path.Combine(compilePath, namespaceName + ".clj.dll");

                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(compilePath);
                File.WriteAllText(sourcePath, $"(ns {namespaceName})\n{SampleBody}");

                return new AotSample(workDir, sourceRoot, compilePath, namespaceName, assemblyPath);
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
    }
}

#endif
