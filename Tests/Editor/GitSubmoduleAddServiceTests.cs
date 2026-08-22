using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    public sealed class GitSubmoduleAddServiceTests
    {
        [Test]
        public void ValidateInput_RejectsUnsafeRepositoryAndInvalidPackageInputs()
        {
            Assert.That(
                GitSubmoduleAddService.ValidateInput(
                    string.Empty,
                    "com.example.package",
                    string.Empty),
                Is.EqualTo("Git URL is required."));
            Assert.That(
                GitSubmoduleAddService.ValidateInput(
                    "http://github.com/example/package.git",
                    "com.example.package",
                    string.Empty),
                Does.Contain("Plain HTTP"));
            Assert.That(
                GitSubmoduleAddService.ValidateInput(
                    "https://github.com/example/package.git",
                    "Invalid Package",
                    string.Empty),
                Is.EqualTo(GitSubmoduleAddService.PackageNameRule));
            Assert.That(
                GitSubmoduleAddService.ValidateInput(
                    "https://github.com/example/package.git",
                    "com.example.package",
                    "bad..branch"),
                Does.Contain("Branch name is invalid"));
        }

        [Test]
        public void ValidateInput_AcceptsSecureRepositoryWithUnusedPackagePath()
        {
            string packageName =
                "com.example.discoverytest" +
                Guid.NewGuid().ToString("N").Substring(0, 12);

            Assert.That(
                GitSubmoduleAddService.ValidateInput(
                    "https://github.com/example/package.git",
                    packageName,
                    "main"),
                Is.Empty);
            Assert.That(
                GitSubmoduleAddService.GetPackagePath(packageName),
                Is.EqualTo("Packages/" + packageName));
        }

        [Test]
        public void ValidateInput_RejectsPhysicalEmptyPackageDirectory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleAddValidation-" + Guid.NewGuid().ToString("N"));
            string packageName = "com.example.physical-directory";
            Directory.CreateDirectory(Path.Combine(root, "Packages", packageName));
            try
            {
                using (GitUtility.OverrideProjectRootForTests(root))
                {
                    Assert.That(
                        GitSubmoduleAddService.ValidateInput(
                            "https://github.com/example/package.git",
                            packageName,
                            "main"),
                        Does.Contain("Package path already exists"));
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Test]
        public void PhysicalPresence_IgnoresUnityVirtualPackageMount()
        {
            string packagesDirectory = Path.Combine(GitUtility.ProjectRoot, "Packages");
            string[] physicalEntries = Directory.GetFileSystemEntries(packagesDirectory);
            UpmPackageInfo virtualPackage = UpmPackageInfo.GetAllRegisteredPackages()
                ?.FirstOrDefault(package =>
                    package != null &&
                    !string.IsNullOrWhiteSpace(package.name) &&
                    Directory.Exists(Path.Combine(packagesDirectory, package.name)) &&
                    !physicalEntries.Any(entry => string.Equals(
                        Path.GetFileName(entry),
                        package.name,
                        StringComparison.OrdinalIgnoreCase)));
            if (virtualPackage == null)
                Assert.Ignore("This Editor session does not expose a virtual UPM package mount.");

            Assert.That(
                GitUtility.TryInspectFileSystemEntryPresence(
                    Path.Combine(packagesDirectory, virtualPackage.name),
                    out bool entryExists,
                    out string error,
                    CancellationToken.None),
                Is.True,
                error);
            Assert.That(entryExists, Is.False);
        }
    }
}
