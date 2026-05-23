#if NET9_0_OR_GREATER

using System;
using System.IO;
using System.Reflection;
using clojure.lang;
using NUnit.Framework;

namespace Clojure.Tests.LibTests
{
    [TestFixture]
    public class GenClassMainTests
    {
        static readonly IFn Eval = RT.var("clojure.core", "eval");
        static readonly IFn ReadString = RT.var("clojure.core", "read-string");

        [OneTimeSetUp]
        public void Setup()
        {
            RT.Init();
        }

        private object EvalClj(string code)
        {
            return Eval.invoke(ReadString.invoke(code));
        }

        [Test]
        public void ModernPersistedAotSupportsGenClassMainMethodEmission()
        {
            var namespaceName = UniqueNamespaceName("testmain");
            var compilePath = Path.Combine(Path.GetTempPath(), "clj-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(compilePath);
            DeleteFallbackArtifacts(namespaceName);

            try
            {
                CompileGenClassMainNamespace(
                    compilePath,
                    namespaceName,
                    @"(defn -main [& args] (str ""hello""))");

                var namespaceAssemblyPath = Path.Combine(compilePath, namespaceName + ".cljr.dll");
                var executableAssemblyPath = Path.Combine(compilePath, namespaceName + ".exe");

                Assert.That(File.Exists(namespaceAssemblyPath), Is.True,
                    "gen-class :main true should still persist the implementing namespace DLL.");
                Assert.That(File.Exists(executableAssemblyPath), Is.True,
                    "gen-class :main true should produce an executable assembly under *compile-path*.");
                AssertNoFallbackExecutable(namespaceName);

                Assembly assembly = Assembly.LoadFrom(executableAssemblyPath);
                Type mainType = assembly.GetType(namespaceName, throwOnError: true);
                MethodInfo mainMethod = mainType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);

                Assert.That(mainMethod, Is.Not.Null, "Generated gen-class type should expose a static Main method.");
                Assert.That(mainMethod.ReturnType, Is.EqualTo(typeof(void)), "Generated Main should return void.");

                ParameterInfo[] parameters = mainMethod.GetParameters();
                Assert.That(parameters, Has.Length.EqualTo(1), "Generated Main should accept one argument.");
                Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(string[])),
                    "Generated Main argument should be a string array.");
            }
            finally
            {
                try { Directory.Delete(compilePath, true); } catch { }
                DeleteFallbackArtifacts(namespaceName);
            }
        }

        [Test]
        public void ModernPersistedAotSupportsGenClassMainEntryPointEmission()
        {
            var namespaceName = UniqueNamespaceName("testinit");
            var compilePath = Path.Combine(Path.GetTempPath(), "clj-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(compilePath);
            DeleteFallbackArtifacts(namespaceName);

            try
            {
                CompileGenClassMainNamespace(
                    compilePath,
                    namespaceName,
                    @"(defn -main [& args] (str ""works""))");

                var executableAssemblyPath = Path.Combine(compilePath, namespaceName + ".exe");

                Assert.That(File.Exists(executableAssemblyPath), Is.True,
                    "gen-class :main true should produce an executable assembly under *compile-path*.");
                AssertNoFallbackExecutable(namespaceName);

                Assembly assembly = Assembly.LoadFrom(executableAssemblyPath);
                MethodInfo entryPoint = assembly.EntryPoint;

                Assert.That(entryPoint, Is.Not.Null,
                    "Generated executable assembly should have an entry point.");
                Assert.That(entryPoint.Name, Is.EqualTo("Main"));
                Assert.That(entryPoint.DeclaringType.FullName, Is.EqualTo(namespaceName));

                ParameterInfo[] parameters = entryPoint.GetParameters();
                Assert.That(parameters, Has.Length.EqualTo(1), "Generated entry point should accept one argument.");
                Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(string[])),
                    "Generated entry point argument should be a string array.");
            }
            finally
            {
                try { Directory.Delete(compilePath, true); } catch { }
                DeleteFallbackArtifacts(namespaceName);
            }
        }

        private void CompileGenClassMainNamespace(string compilePath, string namespaceName, string mainDefinition)
        {
            var srcDir = Path.Combine(compilePath, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, namespaceName + ".cljr"),
                $@"(ns {namespaceName} (:gen-class :main true))
                  {mainDefinition}");

            EvalClj($@"
                (binding [*compile-path* ""{EscapeClojureString(compilePath)}""
                          *compile-files* true]
                  (let [old-path (System.Environment/GetEnvironmentVariable ""CLOJURE_LOAD_PATH"")]
                    (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" ""{EscapeClojureString(srcDir)}"")
                    (try
                      (compile '{namespaceName})
                      (finally
                        (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" old-path)))))");
        }

        private static string UniqueNamespaceName(string prefix)
        {
            return prefix + Guid.NewGuid().ToString("N");
        }

        private static string EscapeClojureString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void AssertNoFallbackExecutable(string namespaceName)
        {
            Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), namespaceName + ".exe")), Is.False,
                "gen-class :main true should not leave a fallback executable in the current working directory.");
        }

        private static void DeleteFallbackArtifacts(string namespaceName)
        {
            try { File.Delete(namespaceName + ".exe"); } catch { }
            try { File.Delete(namespaceName + ".cljr.dll"); } catch { }
        }
    }
}

#endif
