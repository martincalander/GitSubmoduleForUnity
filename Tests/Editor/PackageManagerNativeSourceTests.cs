using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [TestFixture]
    [Category("PackageManagerCompatibility")]
    public sealed class PackageManagerNativeSourceTests
    {
        [TestCase("6000.3.22f1", true)]
        [TestCase("6000.5.0f1", true)]
        [TestCase("6000.5.10f1", true)]
        [TestCase("6000.3.21f1", false)]
        [TestCase("6000.3.23f1", false)]
        [TestCase("6000.4.0f1", false)]
        [TestCase("6000.6.0f1", true)]
        [TestCase("6000.6.1f1", false)]
        [TestCase("6000.6.2f1", true)]
        [TestCase("6000.6.3f1", false)]
        [TestCase("6000.6.2b1", false)]
        [TestCase("6000.6.2f2", false)]
        [TestCase("6000.6.02f1", false)]
        [TestCase("6000.6.2f1c1", false)]
        [TestCase("6000.6.0b1", false)]
        [TestCase("6000.6.0f2", false)]
        [TestCase("6000.7.0a4", false)]
        [TestCase("6000.7.0b1", true)]
        [TestCase("6000.7.0b0", false)]
        [TestCase("6000.7.0b2", true)]
        [TestCase("6000.7.0b3", false)]
        [TestCase("6000.7.0b02", false)]
        [TestCase("6000.7.0b2c1", false)]
        [TestCase(" 6000.7.0b2", false)]
        [TestCase("6000.7.0b2 ", false)]
        [TestCase("6000.7.1b1", false)]
        [TestCase("6000.7.0b01", false)]
        [TestCase("6000.07.0b1", false)]
        [TestCase("6000.7.00b1", false)]
        [TestCase("6000.7.0b1c1", false)]
        [TestCase(" 6000.7.0b1", false)]
        [TestCase("6000.7.0b1 ", false)]
        [TestCase("6000.7.0f1", false)]
        [TestCase("7000.0.0a7", true)]
        [TestCase("7000.0.0a6", false)]
        [TestCase("7000.0.0a8", true)]
        [TestCase("7000.0.0a9", true)]
        [TestCase("7000.0.0a10", true)]
        [TestCase("7000.0.0a11", false)]
        [TestCase("7000.0.0a010", false)]
        [TestCase("7000.0.0a10c1", false)]
        [TestCase(" 7000.0.0a10", false)]
        [TestCase("7000.0.0a10 ", false)]
        [TestCase("7000.0.0a09", false)]
        [TestCase("7000.0.0a9c1", false)]
        [TestCase(" 7000.0.0a9", false)]
        [TestCase("7000.0.0a9 ", false)]
        [TestCase("7000.0.0a08", false)]
        [TestCase("7000.0.0a8c1", false)]
        [TestCase(" 7000.0.0a8", false)]
        [TestCase("7000.0.0a8 ", false)]
        [TestCase("7000.0.1a7", false)]
        [TestCase("7000.1.0a7", false)]
        [TestCase("7000.0.0b7", false)]
        [TestCase("7000.0.0f1", false)]
        [TestCase("7000.0.0a07", false)]
        [TestCase("07000.0.0a7", false)]
        [TestCase("7000.00.0a7", false)]
        [TestCase("7000.0.00a7", false)]
        [TestCase("7000.0.0a7c1", false)]
        [TestCase("7000.0.0A7", false)]
        [TestCase(" 7000.0.0a7", false)]
        [TestCase("7000.0.0a7 ", false)]
        [TestCase("6000.5.0f2", false)]
        [TestCase("6000.5.0f01", false)]
        [TestCase("06000.5.0f1", false)]
        [TestCase("6000.5.0b1", false)]
        [TestCase("6000.5.0f1c1", false)]
        [TestCase(" 6000.5.0f1", false)]
        [TestCase("6000.5f1", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void RuntimeSupportGate_AcceptsOnlyValidatedEditors(
            string unityVersion,
            bool expected)
        {
            Assert.That(
                PackageManagerUnityVersionSupport.IsSupported(unityVersion),
                Is.EqualTo(expected));
        }

        [Test]
        public void InstalledGitHubSource_UsesTheCurrentNativePageContract()
        {
            Assert.That(PackageManagerUnityVersionSupport.IsCurrentVersionSupported,
                Is.True, "This test run is diagnostic on Unity " + Application.unityVersion);
            Assert.That(PackageManagerSubmoduleNativePage.IsSupportedContract(out string error),
                Is.True, error);
            Assert.That(PackageManagerSubmoduleNativePage.GetSidebarRefreshTarget(), Is.Not.Null);
            Assert.That(PackageManagerSubmoduleNativePage.GetNativeRefreshTarget(), Is.Not.Null);
        }

        [Test]
        public void NativePageArguments_UseInstalledMembershipWithoutDiscoveryFilters()
        {
            object args = PackageManagerSubmoduleNativePage.CreateExtensionPageArgs();
            Assert.That(ReadField(args, "name"),
                Is.EqualTo(PackageManagerSubmoduleNativePage.ExtensionPageName));
            Assert.That(ReadField(args, "displayName"), Is.EqualTo("GitHub"));
            Assert.That(ReadField(args, "supportedStatusFilters"), Is.Empty);
            Assert.That(ReadField(args, "supportedSortOptions"), Has.Length.EqualTo(2));
            Assert.That(ReadField(args, "refreshOptions").ToString(), Is.EqualTo("UpmList"));

            var predicate = (Delegate)ReadField(args, "filter");
            Assert.That(predicate.Method.DeclaringType, Is.EqualTo(typeof(PackageManagerPackageStatus)));
            Assert.That(predicate.DynamicInvoke(new object[] { null }), Is.False);
        }

        [TestCase("Extension/git-submodule-manager", true)]
        [TestCase("Extension/another-package", false)]
        [TestCase("InProject", false)]
        [TestCase("Extension/Git-Submodule-Manager", false)]
        [TestCase(null, false)]
        public void GitHubPageIdentity_DoesNotClaimAnotherNativePage(string id, bool expected)
        {
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(new NativePageStub(id)),
                Is.EqualTo(expected));
            Assert.That(PackageManagerSubmoduleNativePage.IsGitHubPage(null), Is.False);
        }

        private static object ReadField(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing native page argument " + name);
            return field.GetValue(instance);
        }

        private sealed class NativePageStub
        {
            public string id { get; }
            internal NativePageStub(string id) { this.id = id; }
        }
    }
}
