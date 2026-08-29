using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MartinCalander.GitSubmoduleManager.Installer
{
    [Serializable]
    internal sealed class InstallerAssetEvidence
    {
        public string path;
        public string guid;
        public string sha256;
        public bool directory;
    }

    [Serializable]
    internal sealed class InstallerOperationState
    {
        public int schemaVersion = 1;
        public string operationId;
        public string targetVersion;
        public string stage;
        public string originalManifestSha256;
        public string writtenManifestSha256;
        public string manifestBackupPath;
        public string rollbackBackupPath;
        public long startedUtcTicks;
        public double resolveRequestedAt;
        public double matchingPackageSince;
        public string error;
        public InstallerAssetEvidence[] assets;
    }

    [InitializeOnLoad]
    internal static class GitSubmoduleManagerInstallerBootstrap
    {
        internal const string TargetVersion = "__PACKAGE_VERSION__";
        internal const string InstallerRoot = "Assets/GitSubmoduleManagerInstaller";

        private const string MainAssemblyName =
            "MartinCalander.GitSubmoduleManager.Editor";
        private const string JournalRelativePath =
            "Library/GitSubmoduleManagerInstaller/operation.json";
        private const int MaximumJournalByteCount = 256 * 1024;
        private const double VerificationTimeoutSeconds = 600d;
        private const double VerificationIntervalSeconds = 1d;
        private const double QuietSuccessSeconds = 1d;

        private const string Prepared = "ManifestWritePrepared";
        private const string ResolvePrepared = "ResolvePrepared";
        private const string ResolveRequested = "ResolveRequested";
        private const string Verifying = "Verifying";
        private const string Cleanup = "Cleanup";
        private const string Failed = "RecoveryBlocked";

        private static readonly object JournalMutationGate = new object();

        private static readonly Regex SupportedCurrentVersion = new Regex(
            @"^6000\.5\.[0-9]+f1$",
            RegexOptions.CultureInvariant);

        private static readonly string[] AssetPaths =
        {
            InstallerRoot,
            InstallerRoot + "/README.txt",
            InstallerRoot + "/InstallerMarker.txt",
            InstallerRoot + "/Editor",
            InstallerRoot + "/Editor/MartinCalander.GitSubmoduleManager.Installer.asmdef",
            InstallerRoot + "/Editor/StrictJson.cs",
            InstallerRoot + "/Editor/GitSubmoduleManagerInstallerManifest.cs",
            InstallerRoot + "/Editor/GitSubmoduleManagerInstaller.cs"
        };

        private static readonly string[] AssetGuids =
        {
            "ba976416d36a1e4d24ac906d76321da7",
            "97049358981d788a10c82f217444047b",
            "22b5bef57d5f086a76a51a8147025a15",
            "792d07690fa4ffbfecb326555afc5d8b",
            "5b963c5e4a8731920905448812f54103",
            "81580cbe9c63724e241fd1e1bea78917",
            "a8a6002061ced0c6e5b37e1dc6158fcd",
            "9d4031aff5460cd8f65fedb455e99409"
        };

        private static InstallerOperationState state;
        private static string loadError = string.Empty;
        private static string status = string.Empty;
        private static double nextVerificationAt;
        private static bool updateAttached;
        private static bool stateLoaded;
        private static byte[] journalBytes;
        private static bool journalConflict;

        static GitSubmoduleManagerInstallerBootstrap()
        {
            if (Application.isBatchMode)
                return;
            EditorApplication.delayCall += Initialize;
        }

        internal static InstallerOperationState State => state;
        internal static string LoadError => loadError;
        internal static string Status => status;
        internal static bool HasActiveOperation => state != null;

        private static string JournalPath =>
            Path.Combine(InstallerManifest.ProjectRoot, JournalRelativePath);

        private static void Initialize()
        {
            EnsureStateLoaded();
            if (state != null)
                ResumeAfterReload();
            if (AssetDatabase.IsValidFolder(InstallerRoot))
                GitSubmoduleManagerInstallerWindow.ShowOnceAfterImport();
        }

        [MenuItem("Tools/Git Submodule Manager/Installer")]
        internal static void ShowWindow()
        {
            GitSubmoduleManagerInstallerWindow.ShowWindow();
        }

        internal static bool TryPreview(
            out InstallerManifestPlan plan,
            out string error)
        {
            plan = null;
            error = string.Empty;
            EnsureStateLoaded();
            if (state == null && !string.IsNullOrWhiteSpace(loadError) &&
                !File.Exists(JournalPath) && !Directory.Exists(JournalPath))
            {
                stateLoaded = false;
                EnsureStateLoaded();
            }

            if (!string.IsNullOrWhiteSpace(loadError))
            {
                error = loadError +
                        " Resolve or remove the preserved journal before starting another install.";
                return false;
            }

            if (state != null)
            {
                error = "An installer operation is already active.";
                return false;
            }

            if (File.Exists(JournalPath) || Directory.Exists(JournalPath))
            {
                error =
                    "An unrecognized installer recovery journal is present and was preserved. " +
                    "Resolve or remove it before starting another install.";
                return false;
            }

            if (!TryCheckSupportedEditor(out error) ||
                !TryCheckExistingPackage(out error) ||
                !TryCheckInstallerLocation(out error))
            {
                return false;
            }

            return InstallerManifest.TryCreatePlan(TargetVersion, out plan, out error);
        }

        internal static void StartInstall(InstallerManifestPlan preview)
        {
            if (state != null)
            {
                status = "An installer operation is already active.";
                RepaintWindow();
                return;
            }

            if (!TryPreview(out InstallerManifestPlan current, out string error))
            {
                status = error;
                RepaintWindow();
                return;
            }

            if (preview == null ||
                !string.Equals(
                    preview.OriginalSha256,
                    current.OriginalSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    preview.WrittenSha256,
                    current.WrittenSha256,
                    StringComparison.Ordinal))
            {
                status =
                    "Packages/manifest.json changed after the preview. Review the refreshed plan, then click Install again.";
                GitSubmoduleManagerInstallerWindow.SetPreview(current);
                RepaintWindow();
                return;
            }

            string confirmation =
                "Install Git Submodule Manager " + TargetVersion + "?\n\n" +
                current.Summary + "\n\n" +
                "Registry: " + InstallerManifest.RegistryUrl + "\n" +
                "Scope: " + InstallerManifest.PackageName + "\n" +
                "Manifest: " + current.Path + "\n\n" +
                "Unity will contact OpenUPM, download the package, resolve its dependencies, " +
                "and load Editor code. After the exact registry package is verified, the " +
                "unchanged bootstrap assets will be moved to the operating system Trash.";
            if (!EditorUtility.DisplayDialog(
                    "Install Git Submodule Manager",
                    confirmation,
                    "Install " + TargetVersion,
                    "Cancel"))
            {
                status = "Installation cancelled. No changes were made.";
                RepaintWindow();
                return;
            }

            if (!TryCaptureAssets(out InstallerAssetEvidence[] evidence, out error))
            {
                status = error;
                RepaintWindow();
                return;
            }

            string backupPath = string.Empty;
            if (current.ChangesManifest &&
                !InstallerManifest.TryPrepareDisplacedPath(
                    current.Path,
                    "installer-original",
                    out backupPath,
                    out error))
            {
                status = error;
                RepaintWindow();
                return;
            }

            state = new InstallerOperationState
            {
                operationId = Guid.NewGuid().ToString("N"),
                targetVersion = TargetVersion,
                stage = Prepared,
                originalManifestSha256 = current.OriginalSha256,
                writtenManifestSha256 = current.WrittenSha256,
                manifestBackupPath = backupPath,
                rollbackBackupPath = string.Empty,
                startedUtcTicks = DateTime.UtcNow.Ticks,
                error = string.Empty,
                assets = evidence
            };
            if (!TrySaveState(out error, true))
            {
                state = null;
                status = error;
                RepaintWindow();
                return;
            }

            ApplyPreparedManifest();
        }

        internal static void ResumeOperation()
        {
            if (state == null)
                return;
            if (state.stage == Prepared)
                ApplyPreparedManifest();
            else if (state.stage == Failed)
            {
                if (InstallerManifest.TryReadRawBytes(
                        InstallerManifest.ManifestPath,
                        out byte[] current,
                        out _) &&
                    string.Equals(
                        InstallerManifest.Sha256(current),
                        state.originalManifestSha256,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        state.originalManifestSha256,
                        state.writtenManifestSha256,
                        StringComparison.Ordinal))
                {
                    state.stage = Prepared;
                    state.error = string.Empty;
                    if (TrySaveState(out string saveError))
                        ApplyPreparedManifest();
                    else
                        FailOperation(saveError);
                }
                else
                {
                    PrepareResolveRetry();
                }
            }
            else if (state.stage == ResolvePrepared)
                PrepareResolveRetry();
            else if (state.stage == Cleanup)
                CleanupInstallerAssets();
        }

        internal static void RetryVerification()
        {
            if (state == null)
                return;
            if (!TryCheckSupportedEditor(out string error))
            {
                FailOperation(error);
                return;
            }

            PrepareResolveRetry();
        }

        internal static void RestoreOriginalManifest()
        {
            if (state == null)
                return;
            if (!EditorUtility.DisplayDialog(
                    "Restore the original manifest?",
                    "This restores only if Packages/manifest.json still exactly matches the bytes written by this installer. Any later edit blocks restoration and is preserved.",
                    "Restore",
                    "Cancel"))
            {
                return;
            }

            if (!TryRestoreOriginal(out string error))
            {
                FailOperation(error);
                return;
            }

            if (!TryDeleteJournal(out error))
            {
                status = error;
                RepaintWindow();
                return;
            }

            status = "The original manifest was restored. The bootstrap assets were kept.";
            state = null;
            DetachUpdate();
            AssetDatabase.Refresh();
            RepaintWindow();
        }

        private static void ResumeAfterReload()
        {
            if (state.stage == ResolvePrepared)
            {
                AttachUpdate();
                return;
            }

            if (state.stage == ResolveRequested || state.stage == Verifying)
            {
                state.stage = Verifying;
                state.matchingPackageSince = 0d;
                TrySaveState(out _);
                AttachUpdate();
                return;
            }

            if (state.stage == Cleanup)
            {
                EditorApplication.delayCall += CleanupInstallerAssets;
                return;
            }

            status = state.error;
        }

        private static void ApplyPreparedManifest()
        {
            if (state == null || state.stage != Prepared)
                return;
            if (!TryCheckSupportedEditor(out string error) ||
                !TryCheckInstallerLocation(out error) ||
                !InstallerManifest.TryReadRawBytes(
                    InstallerManifest.ManifestPath,
                    out byte[] currentBytes,
                    out error))
            {
                FailOperation(error);
                return;
            }

            string currentHash = InstallerManifest.Sha256(currentBytes);
            if (string.Equals(
                    currentHash,
                    state.writtenManifestSha256,
                    StringComparison.Ordinal))
            {
                if (!TryValidateOriginalBackup(out error))
                {
                    FailOperation(error);
                    return;
                }

                state.stage = ResolvePrepared;
                state.error = string.Empty;
                if (!TrySaveState(out error))
                {
                    FailOperation(error);
                    return;
                }

                AttachUpdate();
                return;
            }

            if (!string.Equals(
                    currentHash,
                    state.originalManifestSha256,
                    StringComparison.Ordinal))
            {
                FailOperation(
                    "Packages/manifest.json no longer matches either the authorized original or installer candidate. Nothing was overwritten.");
                return;
            }

            if (!InstallerManifest.TryCreatePlan(
                    state.targetVersion,
                    out InstallerManifestPlan plan,
                    out error) ||
                !string.Equals(
                    plan.OriginalSha256,
                    state.originalManifestSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    plan.WrittenSha256,
                    state.writtenManifestSha256,
                    StringComparison.Ordinal))
            {
                FailOperation(
                    string.IsNullOrEmpty(error)
                        ? "The authorized manifest plan could not be reproduced exactly. Nothing was overwritten."
                        : error);
                return;
            }

            if (!plan.ChangesManifest)
            {
                state.stage = ResolvePrepared;
                if (!TrySaveState(out error))
                    FailOperation(error);
                else
                    AttachUpdate();
                return;
            }

            if (!InstallerManifest.TryCompareAndSwap(
                    plan.Path,
                    plan.OriginalBytes,
                    plan.WrittenBytes,
                    state.manifestBackupPath,
                    out _,
                    out string displacedPath,
                    out error))
            {
                FailOperation(error);
                return;
            }

            if (!string.Equals(
                    displacedPath,
                    state.manifestBackupPath,
                    PathComparison) ||
                !TryValidateOriginalBackup(out error))
            {
                FailOperation(
                    string.IsNullOrEmpty(error)
                        ? "The manifest recovery file did not match its prepared path. Automatic recovery was stopped."
                        : error);
                return;
            }

            state.stage = ResolvePrepared;
            state.error = string.Empty;
            if (!TrySaveState(out error))
            {
                FailOperation(error);
                return;
            }

            status = "The manifest was updated atomically. Waiting for Unity to become idle.";
            AttachUpdate();
            AssetDatabase.Refresh();
            RepaintWindow();
        }

        private static void PrepareResolveRetry()
        {
            if (state == null)
                return;
            if (!InstallerManifest.TryValidateInstalledManifest(
                    state.targetVersion,
                    out string error))
            {
                FailOperation(error);
                return;
            }

            state.stage = ResolvePrepared;
            state.error = string.Empty;
            state.startedUtcTicks = DateTime.UtcNow.Ticks;
            state.resolveRequestedAt = 0d;
            state.matchingPackageSince = 0d;
            if (!TrySaveState(out error))
            {
                FailOperation(error);
                return;
            }

            AttachUpdate();
            RepaintWindow();
        }

        private static void Tick()
        {
            if (state == null)
            {
                DetachUpdate();
                return;
            }

            if (state.stage == ResolvePrepared)
            {
                if (!EditorIsIdle())
                    return;
                state.stage = ResolveRequested;
                state.startedUtcTicks = DateTime.UtcNow.Ticks;
                state.resolveRequestedAt = EditorApplication.timeSinceStartup;
                state.matchingPackageSince = 0d;
                state.error = string.Empty;
                if (!TrySaveState(out string saveError))
                {
                    FailOperation(saveError);
                    return;
                }

                try
                {
                    status = "Unity is resolving Git Submodule Manager from OpenUPM.";
                    Client.Resolve();
                    state.stage = Verifying;
                    TrySaveState(out _);
                    nextVerificationAt = 0d;
                    RepaintWindow();
                }
                catch (Exception exception)
                {
                    FailOperation("Unity could not start package resolution: " + exception.Message);
                }

                return;
            }

            if (state.stage != ResolveRequested && state.stage != Verifying)
            {
                DetachUpdate();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < nextVerificationAt)
                return;
            nextVerificationAt = now + VerificationIntervalSeconds;

            if (state.resolveRequestedAt <= 0d)
                state.resolveRequestedAt = now;
            if (state.resolveRequestedAt > now)
                state.resolveRequestedAt = now;
            if (DateTime.UtcNow.Ticks - state.startedUtcTicks >
                TimeSpan.FromSeconds(VerificationTimeoutSeconds).Ticks)
            {
                FailOperation(
                    "Unity did not verify the exact OpenUPM package within ten minutes. The manifest and recovery evidence were kept. Retry or restore the original manifest.");
                return;
            }

            if (!TryVerifyInstalledPackage(out string error))
            {
                state.matchingPackageSince = 0d;
                status = "Waiting for Unity package resolution. " + error;
                TrySaveState(out _);
                RepaintWindow();
                return;
            }

            if (!EditorIsIdle())
            {
                state.matchingPackageSince = 0d;
                status = "The exact package is present. Waiting for Unity to become idle.";
                TrySaveState(out _);
                RepaintWindow();
                return;
            }

            if (state.matchingPackageSince <= 0d)
            {
                state.matchingPackageSince = now;
                status = "The exact package is present. Confirming a stable compiled state.";
                TrySaveState(out _);
                RepaintWindow();
                return;
            }

            if (now - state.matchingPackageSince < QuietSuccessSeconds)
                return;

            state.stage = Cleanup;
            state.error = string.Empty;
            if (!TrySaveState(out error))
            {
                FailOperation(error);
                return;
            }

            DetachUpdate();
            CleanupInstallerAssets();
        }

        private static bool TryVerifyInstalledPackage(out string error)
        {
            error = string.Empty;
            if (!InstallerManifest.TryValidateInstalledManifest(
                    state.targetVersion,
                    out error))
            {
                return false;
            }

            PackageManagerPackageInfo match = null;
            int count = 0;
            try
            {
                foreach (PackageManagerPackageInfo package in
                         PackageManagerPackageInfo.GetAllRegisteredPackages())
                {
                    if (!string.Equals(
                            package.name,
                            InstallerManifest.PackageName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    match = package;
                    count++;
                }
            }
            catch (Exception exception)
            {
                error = "Registered packages could not be inspected: " + exception.Message;
                return false;
            }

            if (count != 1 || match == null)
            {
                error = count == 0
                    ? "The package is not registered yet."
                    : "More than one package with the target identity was reported.";
                return false;
            }

            if (!string.Equals(match.version, state.targetVersion, StringComparison.Ordinal))
            {
                error = "Unity registered version " + match.version + ", not " +
                        state.targetVersion + ".";
                return false;
            }

            if (!match.isDirectDependency || match.source != PackageSource.Registry)
            {
                error = "The package is not a direct registry dependency.";
                return false;
            }

            if (match.registry == null ||
                !InstallerManifest.IsTrustedRegistryUrl(match.registry.url))
            {
                error = "The package did not resolve from the trusted OpenUPM endpoint.";
                return false;
            }

            if (match.errors != null && match.errors.Length > 0)
            {
                error = "Unity reports package errors, so the bootstrap will be kept.";
                return false;
            }

            if (!IsMainAssemblyLoaded(match))
            {
                error =
                    "The exact package Editor assembly has not compiled and loaded from Unity's registered package yet.";
                return false;
            }

            return true;
        }

        private static bool TryCheckExistingPackage(out string error)
        {
            error = string.Empty;
            string embeddedPath = Path.Combine(
                InstallerManifest.ProjectRoot,
                "Packages",
                InstallerManifest.PackageName);
            if (!InstallerManifest.TryValidateOwnedPath(embeddedPath, out error))
                return false;
            try
            {
                var packagesDirectory = new DirectoryInfo(
                    Path.Combine(InstallerManifest.ProjectRoot, "Packages"));
                foreach (FileSystemInfo entry in packagesDirectory.EnumerateFileSystemInfos())
                {
                    if (!string.Equals(
                            entry.Name,
                            InstallerManifest.PackageName,
                            PathComparison))
                    {
                        continue;
                    }

                    error =
                        "A project-local package entry already exists at Packages/" +
                        InstallerManifest.PackageName + ". The bootstrap will not replace it.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "The project's Packages directory could not be inspected safely: " +
                        exception.Message;
                return false;
            }

            try
            {
                foreach (PackageManagerPackageInfo package in
                         PackageManagerPackageInfo.GetAllRegisteredPackages())
                {
                    if (!string.Equals(
                            package.name,
                            InstallerManifest.PackageName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (package.isDirectDependency &&
                        package.source == PackageSource.Registry &&
                        string.Equals(package.version, TargetVersion, StringComparison.Ordinal) &&
                        package.registry != null &&
                        InstallerManifest.IsTrustedRegistryUrl(package.registry.url))
                    {
                        return true;
                    }

                    error =
                        "Git Submodule Manager is already registered from a different source or version. The bootstrap will not replace it.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "Registered packages could not be inspected: " + exception.Message;
                return false;
            }

            return true;
        }

        private static bool TryCheckSupportedEditor(out string error)
        {
            string version = Application.unityVersion ?? string.Empty;
            if (string.Equals(version, "6000.3.22f1", StringComparison.Ordinal) ||
                SupportedCurrentVersion.IsMatch(version))
            {
                error = string.Empty;
                return true;
            }

            error =
                "This package supports exactly Unity 6000.3.22f1 and final 6000.5.*f1 releases. " +
                "The current Editor is " + version + ", so the installer will not modify the project.";
            return false;
        }

        private static bool TryCheckInstallerLocation(out string error)
        {
            error = string.Empty;
            if (!AssetDatabase.IsValidFolder(InstallerRoot))
            {
                error = "The bootstrap asset folder is missing or has been moved.";
                return false;
            }

            string fullPath = Path.GetFullPath(
                Path.Combine(InstallerManifest.ProjectRoot, InstallerRoot));
            if (!InstallerManifest.TryValidateOwnedPath(fullPath, out error))
                return false;
            return true;
        }

        private static bool TryCaptureAssets(
            out InstallerAssetEvidence[] evidence,
            out string error)
        {
            evidence = Array.Empty<InstallerAssetEvidence>();
            error = string.Empty;
            var items = new List<InstallerAssetEvidence>();
            var expectedEntries = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < AssetPaths.Length; index++)
            {
                string assetPath = AssetPaths[index];
                string expectedGuid = AssetGuids[index];
                string actualGuid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.Equals(actualGuid, expectedGuid, StringComparison.Ordinal))
                {
                    error = "Installer asset GUID verification failed for " + assetPath + ".";
                    return false;
                }

                string fullPath = Path.GetFullPath(
                    Path.Combine(InstallerManifest.ProjectRoot, assetPath));
                bool directory = Directory.Exists(fullPath);
                if (!directory && !File.Exists(fullPath))
                {
                    error = "Installer asset is missing: " + assetPath;
                    return false;
                }

                if (index > 0)
                    expectedEntries.Add(NormalizeAssetPath(assetPath));
                if (index > 0 || !directory)
                    expectedEntries.Add(NormalizeAssetPath(assetPath + ".meta"));

                if (!TryCaptureOne(assetPath, expectedGuid, directory, out InstallerAssetEvidence item, out error))
                    return false;
                items.Add(item);

                string metaPath = assetPath + ".meta";
                if (!TryCaptureOne(metaPath, string.Empty, false, out item, out error))
                    return false;
                items.Add(item);
            }

            string fullRoot = Path.GetFullPath(
                Path.Combine(InstallerManifest.ProjectRoot, InstallerRoot));
            foreach (string entry in Directory.GetFileSystemEntries(
                         fullRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                string assetPath = NormalizeAssetPath(ToProjectRelativePath(entry));
                if (!expectedEntries.Contains(assetPath))
                {
                    error =
                        "The installer folder contains an unknown file or directory: " +
                        assetPath + ". Nothing was changed.";
                    return false;
                }
            }

            evidence = items.ToArray();
            return true;
        }

        private static bool TryCaptureOne(
            string assetPath,
            string guid,
            bool directory,
            out InstallerAssetEvidence evidence,
            out string error)
        {
            evidence = null;
            error = string.Empty;
            string fullPath = Path.GetFullPath(
                Path.Combine(InstallerManifest.ProjectRoot, assetPath));
            if (!InstallerManifest.TryValidateOwnedPath(fullPath, out error))
                return false;
            try
            {
                FileAttributes attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    directory != ((attributes & FileAttributes.Directory) != 0))
                {
                    error = "Installer asset is linked or has the wrong file type: " + assetPath;
                    return false;
                }

                string hash = string.Empty;
                if (!directory)
                {
                    byte[] bytes = File.ReadAllBytes(fullPath);
                    if (bytes.Length > 1024 * 1024)
                    {
                        error = "Installer asset exceeds the 1 MiB safety limit: " + assetPath;
                        return false;
                    }

                    hash = InstallerManifest.Sha256(bytes);
                }

                evidence = new InstallerAssetEvidence
                {
                    path = NormalizeAssetPath(assetPath),
                    guid = guid ?? string.Empty,
                    sha256 = hash,
                    directory = directory
                };
                return true;
            }
            catch (Exception exception)
            {
                error = "Installer asset could not be inspected: " + assetPath + ". " +
                        exception.Message;
                return false;
            }
        }

        private static void CleanupInstallerAssets()
        {
            if (state == null || state.stage != Cleanup)
                return;
            if (!TryVerifyInstalledPackage(out string error))
            {
                PauseCleanup(error);
                return;
            }

            if (!TryMoveInstallerTreeToTrash(out error))
            {
                PauseCleanup(error);
                return;
            }

            if (!TryRetireManifestBackup(out error))
            {
                PauseCleanup(error);
                return;
            }

            if (!TryDeleteJournal(out error))
            {
                status = error;
                RepaintWindow();
                return;
            }

            state = null;
            status = "Git Submodule Manager " + TargetVersion +
                     " was verified from OpenUPM. The bootstrap assets were moved to Trash.";
            Debug.Log(status);
            AssetDatabase.Refresh();
        }

        private static void PauseCleanup(string error)
        {
            if (state == null)
                return;
            state.stage = Cleanup;
            state.error = string.IsNullOrWhiteSpace(error)
                ? "Safe cleanup paused. Recovery evidence was preserved."
                : error;
            if (!TrySaveState(out string saveError) && !string.IsNullOrWhiteSpace(saveError))
                state.error += " " + saveError;
            status = state.error;
            DetachUpdate();
            RepaintWindow();
        }

        private static bool TryMoveInstallerTreeToTrash(out string error)
        {
            error = string.Empty;
            string quarantineRoot = InstallerRoot + "Cleanup-" + state.operationId;
            if (!TryInstallerTreeEntryExists(
                    InstallerRoot,
                    out bool originalExists,
                    out error) ||
                !TryInstallerTreeEntryExists(
                    quarantineRoot,
                    out bool quarantineExists,
                    out error))
            {
                return false;
            }

            if (originalExists && quarantineExists)
            {
                error =
                    "Both the installer folder and its cleanup quarantine exist. Both were preserved.";
                return false;
            }

            if (!originalExists && !quarantineExists)
                return true;

            string activeRoot = originalExists ? InstallerRoot : quarantineRoot;
            if (!TryValidateCapturedAssetsAtRoot(activeRoot, false, out error) ||
                !TryValidateExactInstallerTree(activeRoot, out error))
            {
                return false;
            }

            bool assetEditing = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                assetEditing = true;
                if (originalExists)
                {
                    string moveError = AssetDatabase.MoveAsset(InstallerRoot, quarantineRoot);
                    if (!string.IsNullOrEmpty(moveError))
                    {
                        error = "The installer tree could not be quarantined: " + moveError;
                        return false;
                    }
                }

                if (!TryValidateCapturedAssetsAtRoot(quarantineRoot, false, out error) ||
                    !TryValidateExactInstallerTree(quarantineRoot, out error))
                {
                    TryRestoreInstallerQuarantine(quarantineRoot);
                    return false;
                }

                if (!AssetDatabase.MoveAssetToTrash(quarantineRoot))
                {
                    TryRestoreInstallerQuarantine(quarantineRoot);
                    error =
                        "The exact quarantined installer tree could not be moved to the operating system Trash.";
                    return false;
                }

                if (!TryInstallerTreeEntryExists(
                        quarantineRoot,
                        out bool remains,
                        out error))
                    return false;
                if (remains)
                {
                    error = "The installer cleanup quarantine still exists after the Trash operation.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                TryRestoreInstallerQuarantine(quarantineRoot);
                error = "The installer tree could not be cleaned safely: " +
                        exception.Message;
                return false;
            }
            finally
            {
                if (assetEditing)
                    AssetDatabase.StopAssetEditing();
            }
        }

        private static void TryRestoreInstallerQuarantine(string quarantineRoot)
        {
            try
            {
                if (AssetDatabase.IsValidFolder(quarantineRoot) &&
                    !AssetDatabase.IsValidFolder(InstallerRoot))
                {
                    AssetDatabase.MoveAsset(quarantineRoot, InstallerRoot);
                }
            }
            catch
            {
            }
        }

        private static bool TryValidateCapturedAssetsAtRoot(
            string mappedRoot,
            bool requireAll,
            out string error)
        {
            error = string.Empty;
            if (state.assets == null || state.assets.Length == 0)
            {
                error = "Installer asset evidence is missing.";
                return false;
            }

            foreach (InstallerAssetEvidence evidence in state.assets)
            {
                string mappedPath = MapEvidencePath(evidence.path, mappedRoot);
                if (string.IsNullOrEmpty(mappedPath) ||
                    !TryAssetEntryExists(mappedPath, out bool exists, out error))
                {
                    if (string.IsNullOrEmpty(error))
                        error = "Installer cleanup evidence contains an invalid path.";
                    return false;
                }

                if (!exists)
                {
                    if (requireAll)
                    {
                        error = "Installer asset is missing: " + mappedPath;
                        return false;
                    }

                    continue;
                }

                string fullPath = ToFullPath(mappedPath);
                if (!InstallerManifest.TryValidateOwnedPath(fullPath, out error))
                    return false;
                try
                {
                    FileAttributes attributes = File.GetAttributes(fullPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        evidence.directory !=
                        ((attributes & FileAttributes.Directory) != 0))
                    {
                        error = "Installer cleanup encountered a linked or wrong-type asset: " +
                                mappedPath;
                        return false;
                    }

                    if (!evidence.directory)
                    {
                        byte[] bytes = File.ReadAllBytes(fullPath);
                        if (bytes.Length > 1024 * 1024 ||
                            !string.Equals(
                                InstallerManifest.Sha256(bytes),
                                evidence.sha256,
                                StringComparison.Ordinal))
                        {
                            error =
                                "Installer asset changed after consent and was preserved: " +
                                mappedPath;
                            return false;
                        }
                    }

                    if (!string.IsNullOrEmpty(evidence.guid) &&
                        string.Equals(mappedRoot, InstallerRoot, StringComparison.Ordinal) &&
                        !string.Equals(
                            AssetDatabase.AssetPathToGUID(mappedPath),
                            evidence.guid,
                            StringComparison.Ordinal))
                    {
                        error =
                            "Installer asset GUID changed after consent and was preserved: " +
                            mappedPath;
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    error = "Installer cleanup could not inspect " + mappedPath + ": " +
                            exception.Message;
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateExactInstallerTree(
            string mappedRoot,
            out string error)
        {
            error = string.Empty;
            string fullRoot = ToFullPath(mappedRoot);
            if (!Directory.Exists(fullRoot))
                return true;

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (InstallerAssetEvidence evidence in state.assets)
            {
                string mapped = MapEvidencePath(evidence.path, mappedRoot);
                if (!string.Equals(mapped, mappedRoot, StringComparison.Ordinal) &&
                    !string.Equals(mapped, mappedRoot + ".meta", StringComparison.Ordinal))
                {
                    expected.Add(mapped);
                }
            }

            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(
                             fullRoot,
                             "*",
                             SearchOption.AllDirectories))
                {
                    string assetPath = NormalizeAssetPath(ToProjectRelativePath(entry));
                    if (!expected.Contains(assetPath))
                    {
                        error =
                            "The installer tree contains an unknown entry and was preserved: " +
                            assetPath;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "The installer tree could not be enumerated safely: " +
                        exception.Message;
                return false;
            }
        }

        private static string MapEvidencePath(string path, string mappedRoot)
        {
            if (string.Equals(path, InstallerRoot, StringComparison.Ordinal))
                return mappedRoot;
            if (string.Equals(path, InstallerRoot + ".meta", StringComparison.Ordinal))
                return mappedRoot + ".meta";
            string prefix = InstallerRoot + "/";
            return path != null && path.StartsWith(prefix, StringComparison.Ordinal)
                ? mappedRoot + path.Substring(InstallerRoot.Length)
                : string.Empty;
        }

        private static bool TryAssetEntryExists(
            string assetPath,
            out bool exists,
            out string error)
        {
            exists = false;
            error = string.Empty;
            try
            {
                string fullPath = ToFullPath(assetPath);
                string parent = Path.GetDirectoryName(fullPath);
                string leaf = Path.GetFileName(fullPath);
                if (!InstallerManifest.TryValidateOwnedPath(fullPath, out error))
                    return false;
                if (!Directory.Exists(parent))
                    return true;

                foreach (FileSystemInfo entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
                {
                    if (string.Equals(entry.Name, leaf, PathComparison))
                    {
                        exists = true;
                        return true;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "An installer asset path could not be inspected safely: " +
                        exception.Message;
                return false;
            }
        }

        private static bool TryInstallerTreeEntryExists(
            string assetRoot,
            out bool exists,
            out string error)
        {
            exists = false;
            if (!TryAssetEntryExists(assetRoot, out bool rootExists, out error) ||
                !TryAssetEntryExists(assetRoot + ".meta", out bool metaExists, out error))
            {
                return false;
            }

            exists = rootExists || metaExists;
            return true;
        }

        private static bool TryRetireManifestBackup(out string error)
        {
            if (string.IsNullOrEmpty(state.manifestBackupPath))
            {
                error = string.Empty;
                return true;
            }

            return TryRetireRecoveryFile(
                state.manifestBackupPath,
                state.originalManifestSha256,
                "original manifest",
                out error);
        }

        private static bool TryRetireRollbackBackup(out string error)
        {
            if (string.IsNullOrEmpty(state.rollbackBackupPath))
            {
                error = string.Empty;
                return true;
            }

            return TryRetireRecoveryFile(
                state.rollbackBackupPath,
                state.writtenManifestSha256,
                "rollback",
                out error);
        }

        private static bool TryRetireRecoveryFile(
            string path,
            string expectedSha256,
            string label,
            out string error)
        {
            error = string.Empty;
            if (!TryOwnedFileEntryExists(path, out bool exists, out error))
                return false;
            if (!exists)
                return true;
            if (!InstallerManifest.TryReadRawBytes(
                    path,
                    out byte[] bytes,
                    out error) ||
                !string.Equals(
                    InstallerManifest.Sha256(bytes),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(error))
                    error = "The " + label + " recovery file changed and was preserved.";
                return false;
            }

            if (!InstallerManifest.TryDeleteExactOwnedFile(
                    path,
                    bytes))
            {
                error =
                    "The exact " + label +
                    " recovery file could not be retired safely and was preserved.";
                return false;
            }

            return true;
        }

        private static bool TryOwnedFileEntryExists(
            string fullPath,
            out bool exists,
            out string error)
        {
            exists = false;
            error = string.Empty;
            try
            {
                string resolved = Path.GetFullPath(fullPath ?? string.Empty);
                if (!InstallerManifest.TryValidateOwnedPath(resolved, out error))
                    return false;
                string parent = Path.GetDirectoryName(resolved);
                if (!Directory.Exists(parent))
                    return true;
                string leaf = Path.GetFileName(resolved);
                foreach (FileSystemInfo entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
                {
                    if (string.Equals(entry.Name, leaf, PathComparison))
                    {
                        exists = true;
                        return true;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "A recovery path could not be inspected safely: " +
                        exception.Message;
                return false;
            }
        }

        private static bool TryValidateOriginalBackup(out string error)
        {
            error = string.Empty;
            if (string.Equals(
                    state.originalManifestSha256,
                    state.writtenManifestSha256,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrEmpty(state.manifestBackupPath) ||
                !File.Exists(state.manifestBackupPath) ||
                !InstallerManifest.TryValidateOwnedPath(state.manifestBackupPath, out error) ||
                !InstallerManifest.TryReadRawBytes(
                    state.manifestBackupPath,
                    out byte[] bytes,
                    out error) ||
                !string.Equals(
                    InstallerManifest.Sha256(bytes),
                    state.originalManifestSha256,
                    StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error =
                        "The exact original manifest recovery bytes could not be verified. Automatic mutation is blocked.";
                }

                return false;
            }

            return true;
        }

        private static bool TryRestoreOriginal(out string error)
        {
            error = string.Empty;
            if (!InstallerManifest.TryReadRawBytes(
                    InstallerManifest.ManifestPath,
                    out byte[] current,
                    out error))
            {
                return false;
            }

            string currentHash = InstallerManifest.Sha256(current);
            if (string.Equals(
                    currentHash,
                    state.originalManifestSha256,
                    StringComparison.Ordinal))
            {
                return TryRetireManifestBackup(out error) &&
                       TryRetireRollbackBackup(out error);
            }

            if (!string.Equals(
                    currentHash,
                    state.writtenManifestSha256,
                    StringComparison.Ordinal) ||
                !TryValidateOriginalBackup(out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error =
                        "The manifest contains later changes. They were preserved and automatic restoration was blocked.";
                }

                return false;
            }

            if (!InstallerManifest.TryReadRawBytes(
                    state.manifestBackupPath,
                    out byte[] original,
                    out error))
            {
                return false;
            }

            string rollbackPath = state.rollbackBackupPath;
            if (string.IsNullOrEmpty(rollbackPath))
            {
                if (!InstallerManifest.TryPrepareDisplacedPath(
                        InstallerManifest.ManifestPath,
                        "installer-rollback",
                        out rollbackPath,
                        out error))
                {
                    return false;
                }

                state.rollbackBackupPath = rollbackPath;
                if (!TrySaveState(out error))
                    return false;
            }
            else if (!TryOwnedFileEntryExists(
                         rollbackPath,
                         out bool rollbackExists,
                         out error) ||
                     rollbackExists)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error =
                        "The prepared rollback recovery path is already occupied. It was preserved.";
                }
                return false;
            }

            if (!InstallerManifest.TryCompareAndSwap(
                    InstallerManifest.ManifestPath,
                    current,
                    original,
                    rollbackPath,
                    out _,
                    out _,
                    out error))
            {
                return false;
            }

            return TryRetireManifestBackup(out error) &&
                   TryRetireRollbackBackup(out error);
        }

        private static bool EditorIsIdle()
        {
            return !Application.isBatchMode &&
                   !EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static bool IsMainAssemblyLoaded(PackageManagerPackageInfo expectedPackage)
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(
                        assembly.GetName().Name,
                        MainAssemblyName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    PackageManagerPackageInfo owner =
                        PackageManagerPackageInfo.FindForAssembly(assembly);
                    if (owner != null && expectedPackage != null &&
                        string.Equals(owner.name, expectedPackage.name, StringComparison.Ordinal) &&
                        string.Equals(owner.version, expectedPackage.version, StringComparison.Ordinal) &&
                        string.Equals(
                            Path.GetFullPath(owner.resolvedPath ?? string.Empty),
                            Path.GetFullPath(expectedPackage.resolvedPath ?? string.Empty),
                            PathComparison))
                    {
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static void AttachUpdate()
        {
            if (updateAttached)
                return;
            updateAttached = true;
            EditorApplication.update += Tick;
        }

        private static void DetachUpdate()
        {
            if (!updateAttached)
                return;
            updateAttached = false;
            EditorApplication.update -= Tick;
        }

        private static void FailOperation(string error)
        {
            string saveError = string.Empty;
            if (state != null)
            {
                state.stage = Failed;
                state.error = string.IsNullOrWhiteSpace(error)
                    ? "The operation stopped safely."
                    : error;
                TrySaveState(out saveError);
            }

            status = string.IsNullOrWhiteSpace(error)
                ? "The operation stopped safely."
                : error;
            if (!string.IsNullOrWhiteSpace(saveError))
                status += " " + saveError;
            DetachUpdate();
            RepaintWindow();
        }

        private static void LoadState()
        {
            state = null;
            journalBytes = null;
            journalConflict = false;
            loadError = string.Empty;
            stateLoaded = true;
            try
            {
                if (!File.Exists(JournalPath))
                {
                    if (Directory.Exists(JournalPath))
                    {
                        loadError =
                            "The installer recovery journal path is occupied by a directory and was preserved.";
                        return;
                    }

                    if (!TryRecoverRetiredJournal(out bool recovered, out loadError) ||
                        !recovered)
                    {
                        return;
                    }
                }
                if (!TryReadJournalBytes(JournalPath, out byte[] bytes, out loadError))
                    return;

                string json = new UTF8Encoding(false, true).GetString(bytes);
                StrictJsonValue root = StrictJsonParser.ParseRootObject(json);
                if (!TryValidateJournalJsonSchema(root, out loadError))
                    return;
                InstallerOperationState loaded =
                    JsonUtility.FromJson<InstallerOperationState>(json);
                if (!IsValidState(loaded, out loadError))
                    return;
                state = loaded;
                journalBytes = bytes;
            }
            catch (Exception exception)
            {
                loadError =
                    "The installer recovery journal is invalid and was preserved: " +
                    exception.Message;
            }
        }

        private static bool TryRecoverRetiredJournal(
            out bool recovered,
            out string error)
        {
            recovered = false;
            error = string.Empty;
            string directory = Path.GetDirectoryName(JournalPath);
            if (!Directory.Exists(directory))
                return true;

            try
            {
                string fileName = Path.GetFileName(JournalPath);
                string prefix = fileName + ".retired.";
                const string suffix = ".tmp";
                var candidates = new List<string>();
                foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
                {
                    if (!file.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                        !file.Name.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string identifier = file.Name.Substring(
                        prefix.Length,
                        file.Name.Length - prefix.Length - suffix.Length);
                    if (identifier.Length == 32 && IsLowerHex(identifier))
                        candidates.Add(file.FullName);
                }

                if (candidates.Count == 0)
                    return true;
                if (candidates.Count != 1)
                {
                    error =
                        "Multiple retired recovery journals exist. They were all preserved for manual inspection.";
                    return false;
                }

                string candidatePath = candidates[0];
                if (!TryReadJournalBytes(candidatePath, out byte[] bytes, out error))
                    return false;
                string json = new UTF8Encoding(false, true).GetString(bytes);
                StrictJsonValue root = StrictJsonParser.ParseRootObject(json);
                if (!TryValidateJournalJsonSchema(root, out error))
                    return false;
                InstallerOperationState recoveredState =
                    JsonUtility.FromJson<InstallerOperationState>(json);
                if (!IsValidState(recoveredState, out error) ||
                    !string.Equals(
                        Path.GetFileName(candidatePath),
                        prefix + recoveredState.operationId + suffix,
                        StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error =
                            "A retired recovery journal does not match its operation identity and was preserved.";
                    }
                    return false;
                }

                if (File.Exists(JournalPath) || Directory.Exists(JournalPath))
                {
                    error =
                        "The canonical recovery journal reappeared while a retired journal was being recovered. Both were preserved.";
                    return false;
                }

                File.Move(candidatePath, JournalPath);
                recovered = true;
                return true;
            }
            catch (Exception exception)
            {
                error = "A retired recovery journal could not be recovered safely: " +
                        exception.Message;
                return false;
            }
        }

        private static bool TryValidateJournalJsonSchema(
            StrictJsonValue root,
            out string error)
        {
            error = string.Empty;
            var expectedRoot = new Dictionary<string, StrictJsonKind>(StringComparer.Ordinal)
            {
                { "schemaVersion", StrictJsonKind.Number },
                { "operationId", StrictJsonKind.String },
                { "targetVersion", StrictJsonKind.String },
                { "stage", StrictJsonKind.String },
                { "originalManifestSha256", StrictJsonKind.String },
                { "writtenManifestSha256", StrictJsonKind.String },
                { "manifestBackupPath", StrictJsonKind.String },
                { "rollbackBackupPath", StrictJsonKind.String },
                { "startedUtcTicks", StrictJsonKind.Number },
                { "resolveRequestedAt", StrictJsonKind.Number },
                { "matchingPackageSince", StrictJsonKind.Number },
                { "error", StrictJsonKind.String },
                { "assets", StrictJsonKind.Array }
            };
            if (!HasExactJsonProperties(root, expectedRoot, out error))
                return false;

            StrictJsonValue assets = root.FindProperty("assets").Value;
            if (assets.Items.Count != AssetPaths.Length * 2)
            {
                error = "The installer recovery journal has an unexpected asset count and was preserved.";
                return false;
            }

            var expectedAsset = new Dictionary<string, StrictJsonKind>(StringComparer.Ordinal)
            {
                { "path", StrictJsonKind.String },
                { "guid", StrictJsonKind.String },
                { "sha256", StrictJsonKind.String }
            };
            foreach (StrictJsonValue asset in assets.Items)
            {
                if (asset.Kind != StrictJsonKind.Object ||
                    asset.Properties.Count != expectedAsset.Count + 1)
                {
                    error = "The installer recovery journal contains malformed asset evidence and was preserved.";
                    return false;
                }

                foreach (KeyValuePair<string, StrictJsonKind> entry in expectedAsset)
                {
                    StrictJsonProperty property = asset.FindProperty(entry.Key);
                    if (property == null || property.Value.Kind != entry.Value)
                    {
                        error = "The installer recovery journal contains malformed asset evidence and was preserved.";
                        return false;
                    }
                }

                StrictJsonProperty directory = asset.FindProperty("directory");
                if (directory == null ||
                    (directory.Value.Kind != StrictJsonKind.True &&
                     directory.Value.Kind != StrictJsonKind.False))
                {
                    error = "The installer recovery journal contains malformed asset evidence and was preserved.";
                    return false;
                }
            }

            return true;
        }

        private static bool HasExactJsonProperties(
            StrictJsonValue value,
            Dictionary<string, StrictJsonKind> expected,
            out string error)
        {
            error = string.Empty;
            if (value == null || value.Kind != StrictJsonKind.Object ||
                value.Properties.Count != expected.Count)
            {
                error = "The installer recovery journal has an unexpected schema and was preserved.";
                return false;
            }

            foreach (StrictJsonProperty property in value.Properties)
            {
                if (!expected.TryGetValue(property.Name, out StrictJsonKind kind) ||
                    property.Value.Kind != kind)
                {
                    error = "The installer recovery journal has an unexpected schema and was preserved.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidState(
            InstallerOperationState candidate,
            out string error)
        {
            error = string.Empty;
            if (candidate == null || candidate.schemaVersion != 1 ||
                !Guid.TryParseExact(candidate.operationId, "N", out _) ||
                !string.Equals(candidate.targetVersion, TargetVersion, StringComparison.Ordinal) ||
                !IsKnownStage(candidate.stage) ||
                !IsSha256(candidate.originalManifestSha256) ||
                !IsSha256(candidate.writtenManifestSha256) ||
                candidate.startedUtcTicks <= 0L ||
                candidate.startedUtcTicks > DateTime.MaxValue.Ticks ||
                !IsFiniteNonnegative(candidate.resolveRequestedAt) ||
                !IsFiniteNonnegative(candidate.matchingPackageSince) ||
                candidate.assets == null ||
                candidate.assets.Length != AssetPaths.Length * 2)
            {
                error = "The installer recovery journal failed structural validation and was preserved.";
                return false;
            }

            bool manifestChanged = !string.Equals(
                candidate.originalManifestSha256,
                candidate.writtenManifestSha256,
                StringComparison.Ordinal);
            if (manifestChanged != !string.IsNullOrEmpty(candidate.manifestBackupPath))
            {
                error = "The installer recovery journal has inconsistent recovery evidence and was preserved.";
                return false;
            }

            if ((!string.IsNullOrEmpty(candidate.manifestBackupPath) &&
                 !InstallerManifest.TryValidatePreparedSiblingPath(
                     InstallerManifest.ManifestPath,
                     candidate.manifestBackupPath,
                     "installer-original",
                     out error)) ||
                (!string.IsNullOrEmpty(candidate.rollbackBackupPath) &&
                 (!string.Equals(candidate.stage, Failed, StringComparison.Ordinal) ||
                  !InstallerManifest.TryValidatePreparedSiblingPath(
                      InstallerManifest.ManifestPath,
                      candidate.rollbackBackupPath,
                      "installer-rollback",
                      out error))))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error =
                        "The installer recovery journal has a recovery path in an invalid stage and was preserved.";
                }
                return false;
            }

            for (int index = 0; index < AssetPaths.Length; index++)
            {
                string assetPath = AssetPaths[index];
                bool directory = string.Equals(assetPath, InstallerRoot, StringComparison.Ordinal) ||
                                 string.Equals(assetPath, InstallerRoot + "/Editor", StringComparison.Ordinal);
                InstallerAssetEvidence asset = candidate.assets[index * 2];
                InstallerAssetEvidence meta = candidate.assets[(index * 2) + 1];
                if (!IsExactAssetEvidence(
                        asset,
                        assetPath,
                        AssetGuids[index],
                        directory) ||
                    !IsExactAssetEvidence(
                        meta,
                        assetPath + ".meta",
                        string.Empty,
                        false))
                {
                    error = "The installer asset evidence is not the exact allowlist and was preserved.";
                    return false;
                }
            }

            return true;
        }

        private static void EnsureStateLoaded()
        {
            if (!stateLoaded)
                LoadState();
        }

        private static bool TrySaveState(
            out string error,
            bool requireJournalAbsent = false)
        {
            error = string.Empty;
            if (journalConflict)
            {
                error =
                    "The installer recovery journal has a preserved write conflict. No further journal writes are allowed in this Editor session.";
                return false;
            }
            if (state == null || !IsValidState(state, out error))
                return false;
            string temporaryPath = string.Empty;
            string backupPath = string.Empty;
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(state, true));
            if (payload.Length > MaximumJournalByteCount)
            {
                error = "The installer recovery journal exceeds its safety limit.";
                return false;
            }

            try
            {
                lock (JournalMutationGate)
                {
                    string directory = Path.GetDirectoryName(JournalPath);
                    if (!InstallerManifest.TryValidateOwnedPath(directory, out error) ||
                        !InstallerManifest.TryValidateOwnedPath(JournalPath, out error))
                    {
                        return false;
                    }

                    Directory.CreateDirectory(directory);
                    if (!InstallerManifest.TryValidateOwnedPath(directory, out error) ||
                        !InstallerManifest.TryValidateOwnedPath(JournalPath, out error) ||
                        !TryCreateJournalSiblingPath(
                            "replacement",
                            out temporaryPath,
                            out error))
                    {
                        return false;
                    }

                    WriteJournalBytes(temporaryPath, payload);

                    if (requireJournalAbsent)
                    {
                        if (journalBytes != null || File.Exists(JournalPath) ||
                            Directory.Exists(JournalPath))
                        {
                            error =
                                "An installer recovery journal appeared before the operation could start. " +
                                "It was preserved and nothing was changed.";
                            return false;
                        }

                        File.Move(temporaryPath, JournalPath);
                        temporaryPath = string.Empty;
                        journalBytes = payload;
                        return true;
                    }

                    if (journalBytes == null ||
                        !TryReadJournalBytes(JournalPath, out byte[] current, out error) ||
                        !InstallerManifest.BytesEqual(current, journalBytes))
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error =
                                "The installer recovery journal changed after it was loaded. " +
                                "The changed bytes were preserved and no update was written.";
                        }

                        journalConflict = true;
                        loadError = error;
                        return false;
                    }

                    if (!TryCreateJournalSiblingPath(
                            "displaced",
                            out backupPath,
                            out error))
                    {
                        return false;
                    }

                    File.Replace(temporaryPath, JournalPath, backupPath, true);
                    temporaryPath = string.Empty;
                    if (!TryReadJournalBytes(
                            backupPath,
                            out byte[] displaced,
                            out string readError) ||
                        !InstallerManifest.BytesEqual(displaced, current))
                    {
                        string detail = string.IsNullOrEmpty(readError)
                            ? "The journal changed at the atomic replacement boundary."
                            : readError;
                        PreserveJournalConflict(backupPath, detail, out error);
                        return false;
                    }

                    journalBytes = payload;
                    InstallerManifest.TryDeleteExactOwnedFile(backupPath, current);
                    backupPath = string.Empty;
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = "The installer recovery journal could not be saved: " +
                        exception.Message;
                if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                {
                    journalConflict = true;
                    loadError = error + " Recovery evidence was preserved at " + backupPath + ".";
                    error = loadError;
                }
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                    InstallerManifest.TryDeleteExactOwnedFile(temporaryPath, payload);
            }
        }

        private static bool TryDeleteJournal(out string error)
        {
            error = string.Empty;
            if (journalConflict)
            {
                error =
                    "The recovery journal has a preserved conflict and was not deleted.";
                return false;
            }

            if (journalBytes == null)
            {
                if (!File.Exists(JournalPath) && !Directory.Exists(JournalPath))
                    return true;
                error = "The recovery journal exists without exact in-memory ownership evidence.";
                return false;
            }

            string claimedPath = JournalPath + ".retired." +
                                 state.operationId + ".tmp";
            try
            {
                lock (JournalMutationGate)
                {
                    if (!TryReadJournalBytes(JournalPath, out byte[] current, out error) ||
                        !InstallerManifest.BytesEqual(current, journalBytes))
                    {
                        if (string.IsNullOrEmpty(error))
                            error = "The recovery journal changed before cleanup and was preserved.";
                        journalConflict = true;
                        loadError = error;
                        return false;
                    }

                    if (!InstallerManifest.TryValidateOwnedPath(claimedPath, out error) ||
                        File.Exists(claimedPath) || Directory.Exists(claimedPath))
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error =
                                "The deterministic recovery journal retirement path is already occupied and was preserved.";
                        }
                        return false;
                    }

                    File.Move(JournalPath, claimedPath);
                    if (!TryReadJournalBytes(claimedPath, out byte[] claimed, out _) ||
                        !InstallerManifest.BytesEqual(claimed, current))
                    {
                        if (!File.Exists(JournalPath) && !Directory.Exists(JournalPath))
                        {
                            try
                            {
                                File.Move(claimedPath, JournalPath);
                            }
                            catch
                            {
                            }
                        }
                        journalConflict = true;
                        loadError =
                            "The recovery journal changed while it was being retired. " +
                            "The moved bytes were preserved at " + claimedPath + ".";
                        error = loadError;
                        return false;
                    }

                    if (!InstallerManifest.TryDeleteExactOwnedFile(
                            claimedPath,
                            current) ||
                        File.Exists(JournalPath) || Directory.Exists(JournalPath))
                    {
                        error =
                            "The exact recovery journal could not be retired without ambiguity. " +
                            "All unresolved evidence was preserved.";
                        journalConflict = true;
                        loadError = error;
                        return false;
                    }

                    journalBytes = null;
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = "The exact recovery journal could not be retired safely: " +
                        exception.Message;
                journalConflict = true;
                loadError = error;
                return false;
            }
        }

        private static bool TryCreateJournalSiblingPath(
            string role,
            out string path,
            out string error)
        {
            path = string.Empty;
            error = string.Empty;
            try
            {
                string directory = Path.GetDirectoryName(JournalPath);
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    string candidate = JournalPath + "." + role + "." +
                                       Guid.NewGuid().ToString("N") + ".tmp";
                    if (File.Exists(candidate) || Directory.Exists(candidate) ||
                        !string.Equals(
                            Path.GetDirectoryName(Path.GetFullPath(candidate)),
                            Path.GetFullPath(directory),
                            PathComparison) ||
                        !InstallerManifest.TryValidateOwnedPath(candidate, out error))
                    {
                        continue;
                    }

                    path = candidate;
                    return true;
                }

                error = "A unique recovery journal operation path could not be created.";
                return false;
            }
            catch (Exception exception)
            {
                error = "A recovery journal operation path could not be created: " +
                        exception.Message;
                return false;
            }
        }

        private static void WriteJournalBytes(string path, byte[] payload)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }
        }

        private static bool TryReadJournalBytes(
            string path,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;
            try
            {
                if (!InstallerManifest.TryValidateOwnedPath(path, out error))
                    return false;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 ||
                    info.Length > MaximumJournalByteCount ||
                    (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    error = "A recovery journal file is missing, linked, empty, or too large.";
                    return false;
                }

                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            error = "A recovery journal file changed while it was read.";
                            bytes = Array.Empty<byte>();
                            return false;
                        }

                        offset += read;
                    }

                    if (stream.ReadByte() >= 0)
                    {
                        error = "A recovery journal file changed while it was read.";
                        bytes = Array.Empty<byte>();
                        return false;
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "A recovery journal file could not be read safely: " +
                        exception.Message;
                return false;
            }
        }

        private static void PreserveJournalConflict(
            string displacedPath,
            string detail,
            out string error)
        {
            string capturedPath = string.Empty;
            try
            {
                if (File.Exists(displacedPath) && File.Exists(JournalPath) &&
                    TryCreateJournalSiblingPath("conflict", out capturedPath, out _))
                {
                    File.Replace(displacedPath, JournalPath, capturedPath, true);
                }
            }
            catch
            {
            }

            journalConflict = true;
            journalBytes = null;
            loadError = (detail ?? "A recovery journal conflict occurred.") +
                        " No recovery bytes were deleted. Inspect " + JournalPath +
                        (string.IsNullOrEmpty(capturedPath)
                            ? " and " + displacedPath + "."
                            : ", " + displacedPath + ", and " + capturedPath + ".");
            error = loadError;
        }

        private static bool IsKnownStage(string stage)
        {
            return stage == Prepared || stage == ResolvePrepared ||
                   stage == ResolveRequested || stage == Verifying ||
                   stage == Cleanup || stage == Failed;
        }

        private static bool IsSha256(string value)
        {
            return value != null && value.Length == 64 && IsLowerHex(value);
        }

        private static bool IsLowerHex(string value)
        {
            if (value == null)
                return false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsExactAssetEvidence(
            InstallerAssetEvidence evidence,
            string path,
            string guid,
            bool directory)
        {
            if (evidence == null ||
                !string.Equals(evidence.path, path, StringComparison.Ordinal) ||
                !string.Equals(evidence.guid ?? string.Empty, guid, StringComparison.Ordinal) ||
                evidence.directory != directory)
            {
                return false;
            }

            return directory
                ? string.IsNullOrEmpty(evidence.sha256)
                : IsSha256(evidence.sha256);
        }

        private static bool IsFiniteNonnegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
        }

        private static string ToFullPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(InstallerManifest.ProjectRoot, assetPath));
        }

        private static string ToProjectRelativePath(string fullPath)
        {
            string root = InstallerManifest.ProjectRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(fullPath);
            if (!resolved.StartsWith(root, PathComparison))
                return string.Empty;
            return resolved.Substring(root.Length);
        }

        private static string NormalizeAssetPath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/');
        }

        private static StringComparison PathComparison =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static void RepaintWindow()
        {
            GitSubmoduleManagerInstallerWindow.RepaintOpenWindow();
        }
    }

    internal sealed class GitSubmoduleManagerInstallerWindow : EditorWindow
    {
        private const string SessionKey =
            "MartinCalander.GitSubmoduleManager.Installer.Shown";
        private static GitSubmoduleManagerInstallerWindow instance;
        private static InstallerManifestPlan preview;
        private static string previewError = string.Empty;
        private Vector2 scroll;

        internal static void ShowOnceAfterImport()
        {
            if (SessionState.GetBool(SessionKey, false))
                return;
            SessionState.SetBool(SessionKey, true);
            ShowWindow();
        }

        internal static void ShowWindow()
        {
            instance = GetWindow<GitSubmoduleManagerInstallerWindow>(true);
            instance.titleContent = new GUIContent("Git Submodule Manager Installer");
            instance.minSize = new Vector2(520f, 480f);
            instance.Show();
            instance.RefreshPreview();
        }

        internal static void SetPreview(InstallerManifestPlan plan)
        {
            preview = plan;
            previewError = string.Empty;
        }

        internal static void RepaintOpenWindow()
        {
            if (instance != null)
                instance.Repaint();
        }

        private void OnEnable()
        {
            instance = this;
            RefreshPreview();
        }

        private void OnDisable()
        {
            if (instance == this)
                instance = null;
        }

        private void RefreshPreview()
        {
            if (GitSubmoduleManagerInstallerBootstrap.HasActiveOperation)
                return;
            if (!GitSubmoduleManagerInstallerBootstrap.TryPreview(
                    out preview,
                    out previewError))
            {
                preview = null;
            }

            Repaint();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(
                "Git Submodule Manager",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Safe OpenUPM bootstrap installer for version " +
                GitSubmoduleManagerInstallerBootstrap.TargetVersion,
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8f);

            EditorGUILayout.HelpBox(
                "Importing this .unitypackage does not change the project or contact the network. " +
                "Installation begins only after you review the exact plan and confirm it.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Registry packages contain code that runs in the Unity Editor. This installer " +
                "accepts only the exact package identity, version, direct dependency, and " +
                "https://package.openupm.com registry provenance.",
                MessageType.Warning);

            EditorGUILayout.Space(8f);
            DrawReadOnlyField("Package", InstallerManifest.PackageName);
            DrawReadOnlyField("Version", GitSubmoduleManagerInstallerBootstrap.TargetVersion);
            DrawReadOnlyField("Registry", InstallerManifest.RegistryUrl);
            DrawReadOnlyField("Scope", InstallerManifest.PackageName);
            DrawReadOnlyField("Manifest", InstallerManifest.ManifestPath);

            InstallerOperationState operation =
                GitSubmoduleManagerInstallerBootstrap.State;
            if (operation == null)
                DrawPreview();
            else
                DrawOperation(operation);

            if (!string.IsNullOrWhiteSpace(GitSubmoduleManagerInstallerBootstrap.LoadError))
            {
                EditorGUILayout.HelpBox(
                    GitSubmoduleManagerInstallerBootstrap.LoadError,
                    MessageType.Error);
            }

            if (!string.IsNullOrWhiteSpace(GitSubmoduleManagerInstallerBootstrap.Status))
            {
                EditorGUILayout.HelpBox(
                    GitSubmoduleManagerInstallerBootstrap.Status,
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private static void DrawReadOnlyField(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(80f));
            EditorGUILayout.SelectableLabel(
                value ?? string.Empty,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPreview()
        {
            EditorGUILayout.Space(10f);
            if (!string.IsNullOrWhiteSpace(previewError))
                EditorGUILayout.HelpBox(previewError, MessageType.Error);
            else if (preview != null)
                EditorGUILayout.HelpBox(preview.Summary, MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh plan", GUILayout.Height(28f)))
                RefreshPreview();
            EditorGUI.BeginDisabledGroup(preview == null);
            if (GUILayout.Button(
                    "Install " + GitSubmoduleManagerInstallerBootstrap.TargetVersion,
                    GUILayout.Height(28f)))
            {
                GitSubmoduleManagerInstallerBootstrap.StartInstall(preview);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawOperation(InstallerOperationState operation)
        {
            EditorGUILayout.Space(10f);
            DrawReadOnlyField("Stage", operation.stage);
            if (!string.IsNullOrWhiteSpace(operation.error))
                EditorGUILayout.HelpBox(operation.error, MessageType.Error);

            if (operation.stage == "ManifestWritePrepared")
            {
                if (GUILayout.Button("Resume authorized install", GUILayout.Height(28f)))
                    GitSubmoduleManagerInstallerBootstrap.ResumeOperation();
            }
            else if (operation.stage == "RecoveryBlocked")
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Retry safely", GUILayout.Height(28f)))
                    GitSubmoduleManagerInstallerBootstrap.ResumeOperation();
                if (GUILayout.Button("Restore original manifest", GUILayout.Height(28f)))
                    GitSubmoduleManagerInstallerBootstrap.RestoreOriginalManifest();
                EditorGUILayout.EndHorizontal();
            }
            else if (operation.stage == "Cleanup")
            {
                if (GUILayout.Button("Retry safe cleanup", GUILayout.Height(28f)))
                    GitSubmoduleManagerInstallerBootstrap.ResumeOperation();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Unity is resolving and verifying the package. You may continue using the Editor, but do not edit Packages/manifest.json until this finishes.",
                    MessageType.Info);
            }
        }
    }
}
