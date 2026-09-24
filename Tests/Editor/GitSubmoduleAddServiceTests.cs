using System;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Editor.Tests
{
    [Parallelizable(ParallelScope.None)]
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

        [TestCase("./local package")]
        [TestCase("../local package")]
        public void RelativeLocalInstall_ProbeAndAddKeepProjectIdentityWhenParentOriginIsElsewhere(
            string relativeUrl)
        {
            const string packageName = "com.example.relative-local";
            const string metaGuid = "0123456789abcdef0123456789abcdef";
            string sandbox = Path.Combine(
                Path.GetTempPath(),
                "GitSubmoduleRelativeInstall-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(sandbox, "project");
            string otherDirectory = Path.Combine(sandbox, "other-current-directory");
            string remote = Path.Combine(sandbox, "remote-store", "parent.git");
            string source = Path.GetFullPath(Path.Combine(project, relativeUrl));
            string previousDirectory = Environment.CurrentDirectory;
            ICommandRunner previousRunner = CliCommandRunner.CurrentRunner;
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(otherDirectory);
            Directory.CreateDirectory(remote);
            Directory.CreateDirectory(source);
            try
            {
                CliCommandRunner.ResetRunner();
                InitializeLocalRepository(source);
                File.WriteAllText(Path.Combine(source, "package.json"),
                    "{\"name\":\"" + packageName + "\",\"version\":\"1.0.0\"}\n");
                File.WriteAllText(Path.Combine(source, "package.json.meta"),
                    "fileFormatVersion: 2\nguid: " + metaGuid + "\n");
                ExpectGit(source, "add -- package.json package.json.meta");
                ExpectGit(source, "commit -m \"Initial package\"");
                string inspectedCommit = ExpectGit(source, "rev-parse HEAD").StdOut.Trim();

                ExpectGit(remote, "init --bare");
                InitializeLocalRepository(project);
                File.WriteAllText(Path.Combine(project, ".gitignore"),
                    "/Library/\n/local package/\n");
                ExpectGit(project, "add -- .gitignore");
                ExpectGit(project, "commit -m \"Initial project\"");
                ExpectGit(project, "remote add origin " + GitUtility.Quote(remote));
                Environment.CurrentDirectory = otherDirectory;

                using (GitUtility.OverrideProjectRootForTests(project))
                using (var probe = new GitSubmoduleInstallProbe())
                {
                    Assert.That(probe.Request(relativeUrl, "main"), Is.True);
                    Assert.That(probe.Current.Url, Is.EqualTo(source),
                        "The probe must bind relative input to the project before starting Git.");
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!probe.Current.IsComplete && DateTime.UtcNow < deadline)
                    {
                        probe.Tick();
                        Thread.Sleep(5);
                    }
                    GitSubmoduleInstallProbeSnapshot snapshot = probe.Current;
                    Assert.That(snapshot.Status, Is.EqualTo(GitSubmoduleInstallProbeStatus.Ready),
                        snapshot.ErrorMessage);
                    Assert.That(snapshot.ManifestMessage, Is.Empty);
                    Assert.That(snapshot.PackageName, Is.EqualTo(packageName));
                    Assert.That(snapshot.InspectedCommit, Is.EqualTo(inspectedCommit));
                    Assert.That(snapshot.PackageManifestMetaVerification,
                        Is.EqualTo(PackageManifestMetaVerification.Verified));
                    Assert.That(snapshot.PackageManifestMetaGuid, Is.EqualTo(metaGuid));

                    Assert.That(PackageDependencyInstallRequestFactory.TryCreateFromProbe(
                        snapshot,
                        relativeUrl,
                        "main",
                        packageName,
                        PackageManagerGitInstallMode.GitSubmodule,
                        out PackageDependencyInstallRequest request,
                        out string requestError,
                        PackageManifestMetaPolicy.RequireVerified), Is.True, requestError);
                    Assert.That(request.RepositoryUrl, Is.EqualTo(source));

                    string packagePath = "Packages/" + packageName;
                    var state = new GitSubmoduleAddTaskState();
                    CommandResult result = GitSubmoduleAddService.RunAddSubmoduleTask(
                        relativeUrl,
                        request.Revision,
                        request.RootPackageName,
                        request.RootVersion,
                        GitUtility.ComputePackageDependencyFingerprint(request.Dependencies),
                        packagePath,
                        state,
                        CancellationToken.None,
                        request.PackageManifestMetaVerification,
                        request.PackageManifestMetaGuid,
                        request.InspectedCommit);

                    Assert.That(result.IsSuccess, Is.True, state.Message + " " + result.StdErr);
                    Assert.That(state.AddedSuccessfully, Is.True, state.Message);
                    Assert.That(state.Outcome, Is.EqualTo(GitOperationCompletionOutcome.Succeeded));
                    Assert.That(ExpectGit(project,
                        "config -f .gitmodules --get " +
                        GitUtility.Quote("submodule." + packagePath + ".url")).StdOut.Trim(),
                        Is.EqualTo(source), "New registration must retain the inspected absolute local source.");
                    Assert.That(ExpectGit(Path.Combine(project, packagePath),
                        "remote get-url origin").StdOut.Trim(), Is.EqualTo(source));
                    Assert.That(ExpectGit(Path.Combine(project, packagePath),
                        "rev-parse HEAD").StdOut.Trim(), Is.EqualTo(inspectedCommit));
                    Assert.That(ExpectGit(project, "remote get-url origin").StdOut.Trim(),
                        Is.EqualTo(remote), "The parent origin must remain unchanged.");
                }
            }
            finally
            {
                Environment.CurrentDirectory = previousDirectory;
                CliCommandRunner.CurrentRunner = previousRunner;
                for (int attempt = 0; attempt < 10 && Directory.Exists(sandbox); attempt++)
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(sandbox, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(sandbox, true);
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        // The completed probe may still be deleting its owned clone.
                        Thread.Sleep(10);
                    }
                }
            }
        }

        private static void InitializeLocalRepository(string directory)
        {
            ExpectGit(directory, "init");
            ExpectGit(directory, "symbolic-ref HEAD refs/heads/main");
            ExpectGit(directory, "config user.name \"Git Submodule Manager Tests\"");
            ExpectGit(directory, "config user.email \"tests@example.invalid\"");
        }

        private static CommandResult ExpectGit(string directory, string arguments)
        {
            CommandResult result = CliCommandRunner.Run("git", arguments, directory, 30000);
            Assert.That(result.IsSuccess, Is.True,
                "git " + arguments + " failed: " + result.StdErr);
            return result;
        }
    }
}
