using System.Diagnostics;

namespace Calander.SubmodulePackageManager.Editor
{
    internal static class GitUtility
    {
        // ---------- Util ----------

        internal static void DeleteSubmodule(string submodule)
        {
            
        }
        
        internal static void AddSubmodule(string submodule)
        {
            
        }
        
        // Placeholders for git submodule commands:

        // git submodule [--quiet] [--cached]
        internal static string Submodule(string arguments, string workingDir)
        {
            // Placeholder: pass through any extra arguments (e.g., --quiet, --cached)
            return RunGitCommand($"submodule {arguments}".TrimEnd(), workingDir);
        }

        // git submodule add [<options>] [--] <repository> [<path>]
        internal static string SubmoduleAdd(string arguments, string workingDir)
        {
            // Placeholder: caller composes options/repository/path into 'arguments'
            return RunGitCommand($"submodule add {arguments}".TrimEnd(), workingDir);
        }

        // git submodule status [--cached] [--recursive] [--] [<path>...]
        internal static string SubmoduleStatus(string arguments, string workingDir)
        {
            // Placeholder: caller composes flags and paths into 'arguments'
            return RunGitCommand($"submodule status {arguments}".TrimEnd(), workingDir);
        }

        // git submodule init [--] [<path>...]
        internal static string SubmoduleInit(string arguments, string workingDir)
        {
            // Placeholder: caller composes paths (if any) into 'arguments'
            return RunGitCommand($"submodule init {arguments}".TrimEnd(), workingDir);
        }

        // git submodule deinit [-f|--force] (--all|[--] <path>...)
        internal static string SubmoduleDeinit(string arguments, string workingDir)
        {
            // Placeholder: caller composes --force/--all/paths into 'arguments'
            return RunGitCommand($"submodule deinit {arguments}".TrimEnd(), workingDir);
        }

        // git submodule update [<options>] [--] [<path>...]
        internal static string SubmoduleUpdate(string arguments, string workingDir)
        {
            // Placeholder: caller composes options and paths into 'arguments'
            return RunGitCommand($"submodule update {arguments}".TrimEnd(), workingDir);
        }

        // git submodule set-branch [<options>] [--] <path>
        internal static string SubmoduleSetBranch(string arguments, string workingDir)
        {
            // Placeholder: caller composes options and <path> into 'arguments'
            return RunGitCommand($"submodule set-branch {arguments}".TrimEnd(), workingDir);
        }

        // git submodule set-url [--] <path> <newurl>
        internal static string SubmoduleSetUrl(string path, string newUrl, string workingDir)
        {
            // Placeholder: basic path + newurl; caller may include additional separators if needed
            return RunGitCommand($"submodule set-url {path} {newUrl}".TrimEnd(), workingDir);
        }

        internal static string RunGitCommand(string arguments, string workingDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(error))
                    UnityEngine.Debug.LogWarning(error);

                return output;
            }
        }


    }
}