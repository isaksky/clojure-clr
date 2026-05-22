using System;
using System.Reflection;
using clojure.lang.CljCompiler.Context;
using NUnit.Framework;

namespace Clojure.Tests.LibTests
{
    [TestFixture]
    public class GeneratedArtifactRegistryTests
    {
        [Test]
        public void TypeDeclarationsPairIndependentBackendOrdinals()
        {
            GeneratedArtifactRegistry registry = new();

            GeneratedTypeRecord persisted = registry.DeclareType(
                GeneratedArtifactBackend.Persisted,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn");

            GeneratedTypeRecord eval = registry.DeclareType(
                GeneratedArtifactBackend.Eval,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn__eval");

            GeneratedTypeRecord secondPersisted = registry.DeclareType(
                GeneratedArtifactBackend.Persisted,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn__1");

            GeneratedTypeRecord secondEval = registry.DeclareType(
                GeneratedArtifactBackend.Eval,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn__eval_1");

            Assert.That(eval, Is.SameAs(persisted));
            Assert.That(persisted.Id.Ordinal, Is.EqualTo(0));
            Assert.That(persisted.GetRuntimeName(GeneratedArtifactBackend.Persisted), Is.EqualTo("sample$ns$fn"));
            Assert.That(persisted.GetRuntimeName(GeneratedArtifactBackend.Eval), Is.EqualTo("sample$ns$fn__eval"));

            Assert.That(secondEval, Is.SameAs(secondPersisted));
            Assert.That(secondPersisted.Id.Ordinal, Is.EqualTo(1));
            Assert.That(registry.Types, Has.Count.EqualTo(2));
        }

        [Test]
        public void MemberDeclarationsPairIndependentBackendOrdinals()
        {
            GeneratedArtifactRegistry registry = new();
            GeneratedTypeRecord owner = registry.DeclareType(
                GeneratedArtifactBackend.Persisted,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn");
            registry.DeclareType(
                GeneratedArtifactBackend.Eval,
                "sample/ns.clj",
                "sample$ns$fn",
                "sample$ns$fn__eval");

            MethodInfo persistedToString = typeof(string).GetMethod(nameof(ToString), Type.EmptyTypes);
            MethodInfo evalToString = typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes);

            GeneratedMemberRecord persisted = registry.RegisterMember(
                GeneratedArtifactBackend.Persisted,
                owner.Id,
                GeneratedMemberKind.Method,
                "invoke",
                persistedToString);

            GeneratedMemberRecord eval = registry.RegisterMember(
                GeneratedArtifactBackend.Eval,
                owner.Id,
                GeneratedMemberKind.Method,
                "invoke",
                evalToString);

            Assert.That(eval, Is.SameAs(persisted));
            Assert.That(persisted.Id.Ordinal, Is.EqualTo(0));
            Assert.That(persisted.GetMember(GeneratedArtifactBackend.Persisted), Is.SameAs(persistedToString));
            Assert.That(persisted.GetMember(GeneratedArtifactBackend.Eval), Is.SameAs(evalToString));
            Assert.That(owner.Members, Has.Count.EqualTo(1));
        }
    }
}
