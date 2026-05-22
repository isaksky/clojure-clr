#if NET9_0_OR_GREATER

using System;
using System.IO;
using System.Reflection;
using clojure.lang;
using NUnit.Framework;
using Compiler = clojure.lang.Compiler;

namespace Clojure.Tests.LibTests
{
    [TestFixture]
    public class AotRegressionTests
    {
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

            string previousLoadPath = Environment.GetEnvironmentVariable(RT.ClojureLoadPathString);
            string testLoadPath = string.IsNullOrEmpty(previousLoadPath)
                ? sourceRoot
                : sourceRoot + Path.PathSeparator + previousLoadPath;

            Var compilerOptionsVar = Var.find(Symbol.intern("clojure.core", "*compiler-options*"));
            object compilerOptions = RT.assoc(
                compilerOptionsVar.deref(),
                Keyword.intern(null, "direct-linking"),
                false);

            try
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, testLoadPath);
                Var.pushThreadBindings(RT.map(
                    Compiler.CompilePathVar, compilePath,
                    compilerOptionsVar, compilerOptions));

                try
                {
                    Compiler.CompileVar.invoke(Symbol.intern(namespaceName));
                }
                finally
                {
                    Var.popThreadBindings();
                }

                Assert.That(File.Exists(assemblyPath), Is.True, "AOT compilation should persist the namespace DLL.");

                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                Type initType = assembly.GetType("__Init__$" + namespaceName.Replace(".", "$"));
                Assert.That(initType, Is.Not.Null, "Persisted assembly should contain the namespace initializer type.");

                MethodInfo initialize = initType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                Assert.That(initialize, Is.Not.Null, "Namespace initializer should expose public static Initialize().");

                Assert.That(Var.find(Symbol.intern(namespaceName, "invoked")).deref(), Is.EqualTo(42));
                Assert.That(Var.find(Symbol.intern(namespaceName, "after-let")).deref(), Is.EqualTo(Keyword.intern(null, "loaded")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(RT.ClojureLoadPathString, previousLoadPath);
                try { Directory.Delete(workDir, true); } catch { }
            }
        }
    }
}

#endif
