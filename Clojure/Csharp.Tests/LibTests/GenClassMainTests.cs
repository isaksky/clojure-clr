#if NET9_0_OR_GREATER

using System;
using System.IO;
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
        public void ModernPersistedAotRejectsGenClassMainBeforeMainMethodEmission()
        {
            var compilePath = Path.Combine(Path.GetTempPath(), "clj-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(compilePath);

            try
            {
                var srcDir = Path.Combine(compilePath, "src");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "testmain.cljr"),
                    @"(ns testmain (:gen-class :main true))
                      (defn -main [& args] (str ""hello""))");

                Exception ex = Assert.Catch<Exception>(() => EvalClj($@"
                    (binding [*compile-path* ""{compilePath.Replace("\\", "\\\\")}""
                              *compile-files* true]
                      (let [old-path (System.Environment/GetEnvironmentVariable ""CLOJURE_LOAD_PATH"")]
                        (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" ""{srcDir.Replace("\\", "\\\\")}"")
                        (try
                          (compile 'testmain)
                          (finally
                            (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" old-path)))))"));

                InvalidOperationException unsupported = FindException<InvalidOperationException>(ex);

                Assert.That(unsupported, Is.Not.Null, ex.ToString());
                Assert.That(unsupported.Message, Does.Contain("gen-class"));
                Assert.That(unsupported.Message, Does.Contain("persisted AOT"));
                Assert.That(File.Exists(Path.Combine(compilePath, "testmain.exe")), Is.False,
                    "gen-class :main true is intentionally rejected before executable emission in modern persisted AOT.");
                Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "testmain.exe")), Is.False,
                    "Rejected gen-class compilation should not leave a fallback CWD executable.");
            }
            finally
            {
                try { Directory.Delete(compilePath, true); } catch { }
                try { File.Delete("testmain.exe"); } catch { }
                try { File.Delete("testmain.cljr.dll"); } catch { }
            }
        }

        [Test]
        public void ModernPersistedAotRejectsGenClassMainBeforeEntryPointEmission()
        {
            var compilePath = Path.Combine(Path.GetTempPath(), "clj-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(compilePath);

            try
            {
                var srcDir = Path.Combine(compilePath, "src");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "testinit.cljr"),
                    @"(ns testinit (:gen-class :main true))
                      (defn -main [& args] (str ""works""))");

                Exception ex = Assert.Catch<Exception>(() => EvalClj($@"
                    (binding [*compile-path* ""{compilePath.Replace("\\", "\\\\")}""
                              *compile-files* true]
                      (let [old-path (System.Environment/GetEnvironmentVariable ""CLOJURE_LOAD_PATH"")]
                        (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" ""{srcDir.Replace("\\", "\\\\")}"")
                        (try
                          (compile 'testinit)
                          (finally
                            (System.Environment/SetEnvironmentVariable ""CLOJURE_LOAD_PATH"" old-path)))))"));

                InvalidOperationException unsupported = FindException<InvalidOperationException>(ex);

                Assert.That(unsupported, Is.Not.Null, ex.ToString());
                Assert.That(unsupported.Message, Does.Contain("gen-class"));
                Assert.That(unsupported.Message, Does.Contain("persisted AOT"));
                Assert.That(File.Exists(Path.Combine(compilePath, "testinit.exe")), Is.False,
                    "Rejected gen-class compilation should not produce an executable entry point.");
                Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "testinit.exe")), Is.False,
                    "Rejected gen-class compilation should not leave a fallback CWD executable.");
            }
            finally
            {
                try { Directory.Delete(compilePath, true); } catch { }
                try { File.Delete("testinit.exe"); } catch { }
                try { File.Delete("testinit.cljr.dll"); } catch { }
            }
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
    }
}

#endif
