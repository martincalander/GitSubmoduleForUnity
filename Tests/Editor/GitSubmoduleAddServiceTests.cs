using System;
using NUnit.Framework;

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
    }
}
