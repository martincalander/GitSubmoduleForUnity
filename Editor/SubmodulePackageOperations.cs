using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class SubmodulePackageOperations
    {
        private const int MaximumOutput = 2 * 1024 * 1024;
        private readonly string root;
        private readonly bool allowLocalSources;
        private readonly HashSet<string> installedPackageNames;
        private readonly Action<string> beforeStep;

        internal SubmodulePackageOperations(string projectRoot, bool allowLocalSources = false, IEnumerable<string> installedPackageNames = null, Action<string> beforeStep = null)
        {
            root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            this.allowLocalSources = allowLocalSources;
            this.installedPackageNames = new HashSet<string>(installedPackageNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            this.beforeStep = beforeStep;
        }

        internal string Install(string source, CancellationToken token = default)
        {
            ValidateProject(token);
            using (AcquireOperationLock(token))
            {
                RequireSingleProjectWorktree(token);
                ParseSource(source, out string repository, out string branch);
                ValidateModules(token);
                if (branch.Length != 0)
                    Git(root, token, "check-ref-format", "--branch", branch);
                string staging = Path.Combine(root, "Library", "SubmodulePackageOperations", Guid.NewGuid().ToString("N"));
                CheckPath(staging);
                Directory.CreateDirectory(staging);
                string checkout = Path.Combine(staging, "checkout");
                bool moved = false;
                string destination = null;
                try
                {
                    var arguments = new List<string> { "clone", "--no-recurse-submodules" };
                    if (branch.Length != 0)
                        arguments.AddRange(new[] { "--branch", branch });
                    arguments.AddRange(new[] { "--", repository, checkout });
                    Git(root, token, arguments.ToArray());
                    if (branch.Length == 0)
                        branch = Git(checkout, token, "symbolic-ref", "--short", "HEAD").Trim();
                    Git(checkout, token, "show-ref", "--verify", "refs/remotes/origin/" + branch);
                    RejectNestedSubmodules(checkout, token);
                    string name = PackageManifestDocument.Read(Path.Combine(checkout, "package.json")).ValidatePackage();
                    string relative = Relative(name);
                    destination = Path.Combine(root, relative);
                    ValidateDestination(name, destination, token);
                    ValidateModules(token);
                    string registration = relative;
                    string modulesDirectory = Git(root, token, "rev-parse", "--git-path", "modules").Trim();
                    if (!Path.IsPathRooted(modulesDirectory))
                        modulesDirectory = Path.Combine(root, modulesDirectory);
                    CheckPath(Path.Combine(modulesDirectory, registration));
                    if (Directory.Exists(Path.Combine(modulesDirectory, registration)) || File.Exists(Path.Combine(modulesDirectory, registration)))
                        registration += "-" + Guid.NewGuid().ToString("N");
                    CheckPath(Path.Combine(modulesDirectory, registration));
                    token.ThrowIfCancellationRequested();
                    Directory.Move(checkout, destination);
                    moved = true;
                    beforeStep?.Invoke("install.before-registration");
                    CheckPath(Path.Combine(modulesDirectory, registration));
                    Git(root, token, "submodule", "add", "--name", registration, "-b", branch, "--", repository, relative);
                    Git(root, token, "submodule", "absorbgitdirs", "--", relative);
                    ValidateRegistration(name, destination, token);
                    Directory.Delete(staging);
                    return name;
                }
                catch (Exception exception)
                {
                    string retained = moved ? destination : staging;
                    throw new InvalidOperationException("Submodule installation did not finish. Any checkout and Git data have been retained at " + retained + ". " + exception.Message, exception);
                }
            }
        }

        internal void ValidateRemoval(string packageName, string resolvedPath, CancellationToken token = default)
        {
            ValidateProject(token);
            using (AcquireOperationLock(token))
            {
                RequireSingleProjectWorktree(token);
                PrepareRemoval(packageName, resolvedPath, token);
            }
        }

        internal void Remove(string packageName, string resolvedPath, CancellationToken token = default)
        {
            ValidateProject(token);
            using (AcquireOperationLock(token))
            {
                RequireSingleProjectWorktree(token);
                Removal state = PrepareRemoval(packageName, resolvedPath, token);
                string relative = Relative(packageName);
                string manifest = Path.Combine(root, "Packages", "manifest.json");
                string recovery = Path.Combine(root, "Library", "SubmodulePackageOperations", Guid.NewGuid().ToString("N"));
                CheckPath(recovery);
                Directory.CreateDirectory(recovery);
                File.WriteAllText(Path.Combine(recovery, "manifest.json.before"), state.ManifestBefore, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(recovery, ".gitmodules.before"), state.ModulesBefore, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(recovery, "operation.txt"), "Remove " + relative + "\nGit objects retained in " + state.GitDirectory + "\n", new UTF8Encoding(false));
                bool changedManifest = false;
                string replacedManifest = Path.Combine(recovery, "manifest.json.replaced");
                try
                {
                    // Keep the repository object database before Git removes the working tree.
                    Git(root, token, "submodule", "absorbgitdirs", "--", relative);
                    state.GitDirectory = Git(resolvedPath, token, "rev-parse", "--absolute-git-dir").Trim();
                    File.WriteAllText(Path.Combine(recovery, "operation.txt"), "Remove " + relative + "\nGit objects retained in " + state.GitDirectory + "\n", new UTF8Encoding(false));
                    ValidateRegistration(packageName, resolvedPath, token);
                    RequireCleanCheckout(resolvedPath, token);
                    RequireUnchangedText(manifest, state.ManifestBefore);
                    token.ThrowIfCancellationRequested();
                    beforeStep?.Invoke("remove.before-manifest-write");
                    if (state.ManifestBefore != state.ManifestAfter)
                    {
                        changedManifest = true;
                        ReplaceText(manifest, state.ManifestBefore, state.ManifestAfter, replacedManifest);
                    }
                    beforeStep?.Invoke("remove.before-git-rm");
                    token.ThrowIfCancellationRequested();
                    ValidateModules(token);
                    RequireUnchangedText(Path.Combine(root, ".gitmodules"), state.ModulesBefore);
                    RequireUnchangedText(manifest, state.ManifestAfter);
                    if (Git(root, token, "ls-files", "--stage", "-z", "--", relative) != state.IndexBefore)
                        throw new InvalidOperationException("The package gitlink changed during removal. Review Git status before retrying.");
                    ValidateRegistration(packageName, resolvedPath, token);
                    RequireCleanCheckout(resolvedPath, token);
                    // Quarantine the verified worktree before changing the index. Force applies only to
                    // the gitlink, so staged installs can be removed without deleting working files.
                    string checkout = Path.Combine(recovery, "checkout");
                    Directory.Move(resolvedPath, checkout);
                    beforeStep?.Invoke("remove.after-quarantine");
                    if (Git(root, token, "ls-files", "--stage", "-z", "--", relative) != state.IndexBefore)
                        throw new InvalidOperationException("The package gitlink changed during removal. Review Git status before retrying.");
                    Git(root, token, "rm", "--cached", "--force", "--", relative);
                    RequireUnchangedText(Path.Combine(root, ".gitmodules"), state.ModulesBefore);
                    Git(root, token, "config", "--no-includes", "--file", ".gitmodules", "--remove-section", "submodule." + state.Registration);
                    Git(root, token, "add", "--", ".gitmodules");
                    beforeStep?.Invoke("remove.after-git-rm");
                    string localRegistration = GitAllowMissing(root, token, "config", "--local", "--get-regexp", "^submodule\\." + Regex.Escape(state.Registration) + "\\.");
                    if (localRegistration.Length != 0)
                        Git(root, token, "config", "--local", "--remove-section", "submodule." + state.Registration);
                    if (Directory.Exists(resolvedPath) || Git(root, token, "ls-files", "--stage", "--", relative).Length != 0)
                        throw new InvalidOperationException("Git did not finish removing the package checkout and gitlink.");
                    // Keep the quarantined files and Git object store available for recovery.
                    // The package is no longer under Packages/ and has no submodule registration.
                }
                catch (Exception exception)
                {
                    try
                    {
                        if (changedManifest && Directory.Exists(resolvedPath) && File.Exists(manifest) && PackageManifestDocument.ReadText(manifest, true) == state.ManifestAfter)
                        {
                            string restore = File.Exists(replacedManifest) ? PackageManifestDocument.ReadText(replacedManifest, true) : state.ManifestBefore;
                            ReplaceText(manifest, state.ManifestAfter, restore, Path.Combine(recovery, "manifest.json.cancelled"));
                        }
                    }
                    catch (Exception) { /* Preserve the backup when the manifest cannot be safely restored. */ }
                    throw new InvalidOperationException("Submodule removal did not finish. Review Git status before retrying. Recovery information is at " + recovery + ". " + exception.Message, exception);
                }
            }
        }

        private static void RequireUnchangedText(string path, string expected)
        {
            CheckPath(path);
            if (PackageManifestDocument.ReadText(path, true) != expected)
                throw new InvalidOperationException("The file changed during the submodule operation: " + path + ". Review the project before retrying.");
        }

        private static void ReplaceText(string path, string expected, string replacement, string backup)
        {
            RequireUnchangedText(path, expected);
            string temporary = path + ".submodule-" + Guid.NewGuid().ToString("N");
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    byte[] bytes = new UTF8Encoding(false, true).GetBytes(replacement);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                RequireUnchangedText(path, expected);
                File.Replace(temporary, path, backup);
                // Atomic replacement retains the actual previous file, even if another writer won
                // the narrow race after the final comparison.
                if (PackageManifestDocument.ReadText(backup, true) != expected)
                    throw new InvalidOperationException("The file changed while it was being replaced. Its previous contents are preserved at " + backup + ".");
            }
            finally
            {
                if (created && File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private sealed class Removal
        {
            internal string Registration;
            internal string GitDirectory;
            internal string ManifestBefore;
            internal string ManifestAfter;
            internal string ModulesBefore;
            internal string IndexBefore;
        }

        private Removal PrepareRemoval(string name, string path, CancellationToken token)
        {
            ValidateModules(token);
            string modulesBefore = PackageManifestDocument.ReadText(Path.Combine(root, ".gitmodules"), true);
            string indexBefore = Git(root, token, "ls-files", "--stage", "-z", "--", Relative(name));
            string registration = ValidateRegistration(name, path, token);
            RequireCleanCheckout(path, token);
            string repository = Git(root, token, "config", "--no-includes", "--file", ".gitmodules", "--get", "submodule." + registration + ".url").Trim();
            string origin = Git(path, token, "config", "--get", "remote.origin.url").Trim();
            if (!string.Equals(repository, origin, StringComparison.Ordinal))
                throw new InvalidOperationException("The submodule origin does not match .gitmodules. Review its repository identity with Git before removing it.");
            ParseSource(origin, out _, out string revision);
            if (revision.Length != 0)
                throw new InvalidOperationException("The registered repository URL must not contain a revision suffix.");
            // Fetch only this repository. A stale local remote-tracking ref cannot prove publication.
            Git(path, token, "fetch", "--prune", "--no-tags", "origin", "+refs/heads/*:refs/remotes/origin/*");
            if (Git(path, token, "rev-list", "--max-count=1", "HEAD", "--all", "--not", "--remotes=origin").Length != 0)
                throw new InvalidOperationException("The submodule contains commits that are not published on origin. Push or preserve every local branch before removing it.");
            ValidateRegistration(name, path, token);
            RequireCleanCheckout(path, token);
            string manifestPath = Path.Combine(root, "Packages", "manifest.json");
            PackageManifestDocument document = PackageManifestDocument.Read(manifestPath);
            string before = document.Source;
            return new Removal
            {
                Registration = registration,
                GitDirectory = Git(path, token, "rev-parse", "--absolute-git-dir").Trim(),
                ManifestBefore = before,
                ManifestAfter = document.WithoutDependency(name),
                ModulesBefore = modulesBefore,
                IndexBefore = indexBefore
            };
        }

        private void ValidateProject(CancellationToken token)
        {
            CheckPath(root);
            CheckPath(Path.Combine(root, "Packages"));
            if (!Directory.Exists(Path.Combine(root, "Packages")))
                throw new InvalidOperationException("The Unity project must have a Packages folder.");
            string actual = Git(root, token, "rev-parse", "--show-toplevel").Trim();
            if (!SamePath(actual, root))
                throw new InvalidOperationException("The Unity project must be the root of its Git repository.");
            PackageManifestDocument.Read(Path.Combine(root, "Packages", "manifest.json"));
        }

        private IDisposable AcquireOperationLock(CancellationToken token)
        {
            string gitDirectory = Git(root, token, "rev-parse", "--git-common-dir").Trim();
            if (!Path.IsPathRooted(gitDirectory))
                gitDirectory = Path.GetFullPath(Path.Combine(root, gitDirectory));
            string lockPath = Path.Combine(gitDirectory, "submodule-package-operation.lock");
            CheckPath(lockPath);
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) { throw new InvalidOperationException("Another submodule package operation is running. Wait for it to finish.", exception); }
        }

        private void RequireSingleProjectWorktree(CancellationToken token)
        {
            if (Git(root, token, "worktree", "list", "--porcelain").Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("The project repository has linked Git worktrees that share submodule configuration. Manage its submodules with Git.");
        }

        private void ValidateModules(CancellationToken token)
        {
            string path = Path.Combine(root, ".gitmodules");
            CheckPath(path);
            if (File.Exists(path) && new FileInfo(path).Length > PackageManifestDocument.MaximumBytes)
                throw new InvalidOperationException("The .gitmodules file is too large to validate safely.");
            string index = Git(root, token, "ls-files", "--stage", "-v", "-z", "--", ".gitmodules");
            if (File.Exists(path) && index.Length == 0)
                throw new InvalidOperationException("Stage the existing .gitmodules file before changing submodule packages.");
            RejectHiddenIndexEntries(index);
            string[] entries = index.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length > 1 || (entries.Length == 1 && !Regex.IsMatch(entries[0], @"\AH 100(?:644|755) [0-9a-f]{40,64} 0\t\.gitmodules\z")))
                throw new InvalidOperationException("Resolve the .gitmodules index state before changing submodule packages.");
            if (Git(root, token, "diff", "--name-only", "--", ".gitmodules").Length != 0)
                throw new InvalidOperationException("Stage or restore your .gitmodules changes before changing submodule packages.");
        }

        private void ValidateDestination(string name, string destination, CancellationToken token)
        {
            CheckPath(destination);
            if (installedPackageNames.Contains(name))
                throw new InvalidOperationException("This package is already installed. Remove its existing installation before adding it as a submodule.");
            if (File.Exists(destination) || Directory.Exists(destination) || File.Exists(destination + ".meta"))
                throw new InvalidOperationException("The package destination already exists: " + Relative(name));
            if (PackageManifestDocument.Read(Path.Combine(root, "Packages", "manifest.json")).HasDependency(name))
                throw new InvalidOperationException("This package is already listed in the project manifest. Remove that installation before adding it as a submodule.");
            if (Git(root, token, "ls-files", "--stage", "--", Relative(name)).Length != 0)
                throw new InvalidOperationException("Git already tracks the package destination.");
            foreach (var registration in ReadRegistrations(token))
                if (string.Equals(registration.Value, Relative(name), StringComparison.OrdinalIgnoreCase) || registration.Key == Relative(name))
                    throw new InvalidOperationException("A submodule registration already uses this package destination.");
            foreach (string directory in Directory.GetDirectories(Path.Combine(root, "Packages")))
            {
                string manifest = Path.Combine(directory, "package.json");
                if (!File.Exists(manifest))
                    continue;
                CheckPath(directory);
                if (PackageManifestDocument.Read(manifest).PackageName == name)
                    throw new InvalidOperationException("Another embedded package already declares this package name.");
            }
        }

        private string ValidateRegistration(string name, string path, CancellationToken token)
        {
            string relative = Relative(name);
            if (!SamePath(path, Path.Combine(root, relative)))
                throw new InvalidOperationException("Only a matching direct Packages/<package-name> submodule can be removed.");
            CheckPath(path);
            CheckPath(Path.Combine(path, ".git"));
            if (!Directory.Exists(path) || PackageManifestDocument.Read(Path.Combine(path, "package.json")).ValidatePackage() != name)
                throw new InvalidOperationException("The checkout package.json does not match the selected package.");
            if (!SamePath(Git(path, token, "rev-parse", "--show-toplevel").Trim(), path))
                throw new InvalidOperationException("The package folder is not the root of its own repository.");
            var matches = ReadRegistrations(token).Where(pair => string.Equals(pair.Value, relative, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].Value, relative, StringComparison.Ordinal))
                throw new InvalidOperationException("The package has missing or ambiguous submodule registration.");
            string index = Git(root, token, "ls-files", "--stage", "-z", "--", relative);
            string[] entries = index.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length != 1 || !Regex.IsMatch(entries[0], @"\A160000 [0-9a-f]{40,64} 0\t" + Regex.Escape(relative) + @"\z"))
                throw new InvalidOperationException("The package does not have a single verified Git submodule entry in the project index.");
            string commit = entries[0].Split(' ')[1];
            if (!string.Equals(commit, Git(path, token, "rev-parse", "HEAD").Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("The submodule commit differs from the parent gitlink. Stage or restore that revision with Git before removing it.");
            string gitDirectory = Git(path, token, "rev-parse", "--absolute-git-dir").Trim();
            CheckPath(gitDirectory);
            string modulesDirectory = Git(root, token, "rev-parse", "--git-path", "modules").Trim();
            if (!Path.IsPathRooted(modulesDirectory))
                modulesDirectory = Path.Combine(root, modulesDirectory);
            modulesDirectory = Path.GetFullPath(modulesDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string expectedModuleDirectory = Path.GetFullPath(Path.Combine(modulesDirectory, matches[0].Key));
            bool ownedModuleDirectory = expectedModuleDirectory.StartsWith(modulesDirectory,
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && SamePath(gitDirectory, expectedModuleDirectory);
            if (!SamePath(gitDirectory, Path.Combine(path, ".git")) && !ownedModuleDirectory)
                throw new InvalidOperationException("The submodule Git data is not owned by this project. Manage this checkout with Git to preserve its data.");
            string worktrees = Git(path, token, "worktree", "list", "--porcelain");
            if (worktrees.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("The package repository has additional Git worktrees. Manage it with Git to preserve those checkouts.");
            string superproject = Git(path, token, "rev-parse", "--show-superproject-working-tree").Trim();
            if (superproject.Length != 0 && !SamePath(superproject, root))
                throw new InvalidOperationException("The checkout belongs to another parent repository.");
            return matches[0].Key;
        }

        private Dictionary<string, string> ReadRegistrations(CancellationToken token)
        {
            var registrations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(Path.Combine(root, ".gitmodules")))
                return registrations;
            string values = GitAllowMissing(root, token, "config", "--no-includes", "--null", "--file", ".gitmodules", "--get-regexp", "^submodule\\..*\\.path$");
            foreach (string value in values.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int newline = value.IndexOf('\n');
                if (newline < 0)
                    throw new InvalidDataException("The submodule registrations could not be read.");
                string key = value.Substring(10, newline - 15);
                if (registrations.ContainsKey(key))
                    throw new InvalidDataException("Duplicate submodule registrations need to be resolved with Git.");
                registrations.Add(key, value.Substring(newline + 1));
            }
            return registrations;
        }

        private void RequireCleanCheckout(string path, CancellationToken token)
        {
            string index = Git(path, token, "ls-files", "--stage", "-v", "-z");
            RejectHiddenIndexEntries(index);
            RejectNestedEntries(index, true);
            if (Git(path, token, "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching").Length != 0)
                throw new InvalidOperationException("The submodule has local changes, untracked files, or ignored files. Commit, move, or remove them with Git before uninstalling.");
        }

        private static void RejectHiddenIndexEntries(string index)
        {
            foreach (string entry in index.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                if (char.IsLower(entry[0]) || entry[0] == 'S')
                    throw new InvalidOperationException("Git index flags can hide local changes. Clear assume-unchanged and skip-worktree flags before changing this submodule package.");
        }

        private void RejectNestedSubmodules(string path, CancellationToken token) =>
            RejectNestedEntries(Git(path, token, "ls-files", "--stage", "-z"), false);

        private static void RejectNestedEntries(string index, bool hasFlags)
        {
            if (index.Split('\0').Any(value => value.StartsWith(hasFlags ? "H 160000 " : "160000 ", StringComparison.Ordinal)))
                throw new InvalidOperationException("Packages with nested submodules must be managed with Git to preserve their repository data.");
        }

        private static string Relative(string name)
        {
            if (!PackageManifestDocument.ValidPackageName(name))
                throw new InvalidOperationException("A valid lowercase reverse-domain package name is required.");
            return "Packages/" + name;
        }

        private void ParseSource(string source, out string repository, out string branch)
        {
            if (string.IsNullOrWhiteSpace(source) || source.Length > 4096 || source.Any(char.IsControl) || source.Contains('\\'))
                throw new InvalidOperationException("Enter an HTTPS or SSH Git repository URL, optionally followed by #branch.");
            source = source.StartsWith("git+", StringComparison.Ordinal) ? source.Substring(4) : source;
            int hash = source.IndexOf('#');
            repository = hash >= 0 ? source.Substring(0, hash) : source;
            branch = hash >= 0 ? source.Substring(hash + 1) : string.Empty;
            if (hash >= 0 && (branch.Length == 0 || branch.Contains('#')))
                throw new InvalidOperationException("The URL branch suffix is invalid.");
            if (allowLocalSources && Path.IsPathRooted(repository) && Directory.Exists(repository))
                return;
            if (source.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("The repository URL and branch must not contain whitespace.");
            if (Regex.IsMatch(repository, @"\A[a-zA-Z0-9_][a-zA-Z0-9_.-]*@[a-zA-Z0-9][a-zA-Z0-9.-]*:[a-zA-Z0-9_./-]+\z") && !repository.Contains(".."))
                return;
            if (!Uri.TryCreate(repository, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "https" && uri.Scheme != "ssh") || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath == "/" ||
                uri.Query.Length != 0 || uri.Host.StartsWith("-", StringComparison.Ordinal) || uri.UserInfo.StartsWith("-", StringComparison.Ordinal) ||
                (uri.Scheme == "https" && uri.UserInfo.Length != 0) || uri.UserInfo.Contains(':'))
                throw new InvalidOperationException("Use an HTTPS or SSH repository URL without credentials, queries, or a package subfolder. package.json must be at the repository root.");
        }

        private static bool SamePath(string left, string right) => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static void CheckPath(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked package or repository paths cannot be changed safely: " + current);
            }
        }

        private string Git(string directory, CancellationToken token, params string[] arguments) => RunGit(directory, token, false, arguments);
        private string GitAllowMissing(string directory, CancellationToken token, params string[] arguments) => RunGit(directory, token, true, arguments);

        private string RunGit(string directory, CancellationToken token, bool allowMissing, string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            var all = new List<string> { "-c", "protocol.file.allow=" + (allowLocalSources ? "always" : "never"), "-c", "core.quotepath=false" };
            all.AddRange(arguments);
            var process = new Process();
            Task<string> output = null;
            Task<string> error = null;
            try
            {
                process.StartInfo = new ProcessStartInfo("git", string.Join(" ", all.Select(Quote)))
                {
                    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true)
                };
                var inherited = new List<string>();
                foreach (string key in process.StartInfo.EnvironmentVariables.Keys)
                    if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                        inherited.Add(key);
                foreach (string key in inherited)
                    process.StartInfo.EnvironmentVariables.Remove(key);
                process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                process.StartInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
                process.Start();
                output = Task.Run(() => ReadBounded(process.StandardOutput));
                error = Task.Run(() => ReadBounded(process.StandardError));
                var timer = Stopwatch.StartNew();
                Exception failure = null;
                try
                {
                    while (!process.WaitForExit(50))
                    {
                        token.ThrowIfCancellationRequested();
                        if (timer.Elapsed.TotalSeconds > 120 || output.IsFaulted || error.IsFaulted)
                            throw new InvalidOperationException("Git timed out or returned too much output. Check repository access and try again.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (!Task.WaitAll(new Task[] { output, error }, 1000))
                        throw new InvalidOperationException("Git did not finish writing its output.");
                    if (process.ExitCode != 0 && !(allowMissing && process.ExitCode == 1 && output.Result.Length == 0 && error.Result.Length == 0))
                        throw new InvalidOperationException("Git could not complete " + arguments[0] + ". Check repository access, local changes, and Git configuration.");
                    return output.Result;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }
                finally
                {
                    if (failure != null)
                    {
                        bool unclosedPipes = process.HasExited && (!output.IsCompleted || !error.IsCompleted);
                        string cleanupError = StopGitProcess(process);
                        if (unclosedPipes)
                            cleanupError ??= "Git exited before closing its output pipes; helper processes could not be verified.";
                        try { Task.WaitAll(new Task[] { output, error }, 1000); }
                        catch (AggregateException) { }
                        if (!output.IsCompleted || !error.IsCompleted)
                            cleanupError ??= "Git output readers did not stop.";
                        if (cleanupError != null)
                            throw new InvalidOperationException(failure.Message + " Git helper cleanup could not be confirmed: " + cleanupError + " Review the repository before retrying.", failure);
                    }
                }
            }
            finally
            {
                DisposeAfterReadersComplete(process, output, error);
            }
        }

        private static string StopGitProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return null;
                DateTime started = process.StartTime;
                MethodInfo killTree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
                if (killTree != null)
                    killTree.Invoke(process, new object[] { true });
                else if (Path.DirectorySeparatorChar == '\\')
                    RunCleanupCommand("taskkill", "/PID " + process.Id + " /T /F");
                else
                {
                    // Mono lacks Kill(entireProcessTree). Capture only descendants of this Git
                    // process and stop them leaf-first, verifying PID lifetime before each signal.
                    string listing = RunCleanupCommand("/bin/ps", "-A -o pid=,ppid=");
                    var parents = new Dictionary<int, int>();
                    foreach (string line in listing.Split('\n'))
                    {
                        string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length == 2 && int.TryParse(fields[0], out int id) && int.TryParse(fields[1], out int parent))
                            parents[id] = parent;
                    }
                    var descendants = new List<(int Id, DateTime Start, int Depth)>();
                    foreach (int id in parents.Keys)
                    {
                        int current = id;
                        int depth = 0;
                        var visited = new HashSet<int>();
                        while (parents.TryGetValue(current, out int parent) && visited.Add(current))
                        {
                            depth++;
                            if (parent == process.Id)
                            {
                                try
                                {
                                    using (Process child = Process.GetProcessById(id))
                                        if (child.StartTime >= started)
                                            descendants.Add((id, child.StartTime, depth));
                                }
                                catch (ArgumentException) { }
                                break;
                            }
                            current = parent;
                        }
                    }
                    foreach (var child in descendants.OrderByDescending(value => value.Depth))
                    {
                        try
                        {
                            using (Process owned = Process.GetProcessById(child.Id))
                                if (!owned.HasExited && owned.StartTime == child.Start)
                                    owned.Kill();
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                    if (!process.HasExited && process.StartTime == started)
                        process.Kill();
                }
                return process.WaitForExit(1000) ? null : "The Git process did not stop.";
            }
            catch (Exception exception)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception) { }
                return exception.GetBaseException().Message;
            }
        }

        private static string RunCleanupCommand(string executable, string arguments)
        {
            var helper = new Process();
            Task<string> output = null;
            Task<string> error = null;
            try
            {
                helper.StartInfo = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                helper.Start();
                output = Task.Run(() => ReadBounded(helper.StandardOutput));
                error = Task.Run(() => ReadBounded(helper.StandardError));
                try
                {
                    if (!helper.WaitForExit(1000) || !Task.WaitAll(new Task[] { output, error }, 500) || helper.ExitCode != 0)
                        throw new InvalidOperationException("The process cleanup command did not finish successfully.");
                    return output.Result;
                }
                finally
                {
                    if (!helper.HasExited)
                    {
                        try { helper.Kill(); helper.WaitForExit(500); }
                        catch (Exception) { }
                    }
                }
            }
            finally
            {
                DisposeAfterReadersComplete(helper, output, error);
            }
        }

        private static void DisposeAfterReadersComplete(Process process, Task<string> output, Task<string> error)
        {
            Task[] readers = new Task[] { output, error }.Where(reader => reader != null).ToArray();
            if (readers.All(reader => reader.IsCompleted))
            {
                foreach (Task reader in readers)
                    _ = reader.Exception;
                process.Dispose();
                return;
            }

            // Closing a pipe while another thread reads it can block on CoreCLR. An
            // unverified helper must release its pipes before disposal can finish.
            _ = Task.WhenAll(readers).ContinueWith(completed =>
            {
                _ = completed.Exception;
                try { process.Dispose(); }
                catch (Exception) { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private static string Quote(string value)
        {
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') quoted.Append('\\', slashes * 2 + 1);
                else quoted.Append('\\', slashes);
                quoted.Append(character);
                slashes = 0;
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static string ReadBounded(StreamReader reader)
        {
            var value = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (value.Length + count > MaximumOutput)
                    throw new InvalidOperationException("Git output exceeded its limit.");
                value.Append(buffer, 0, count);
            }
            return value.ToString();
        }
    }
}
