using System;
using UnityEngine;

namespace GitPackageManager.Editor
{
    internal static class CliInstaller
    {
        internal static bool TryInstallGit(out string output, out string error)
        {
            return TryInstall(ToolKind.Git, out output, out error);
        }

        internal static bool TryInstallGh(out string output, out string error)
        {
            return TryInstall(ToolKind.GitHubCli, out output, out error);
        }

        internal static string GetInstallHint(ToolKind tool)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return tool == ToolKind.Git ? "brew install git" : "brew install gh";
                case RuntimePlatform.WindowsEditor:
                    return tool == ToolKind.Git ? "winget install --id Git.Git -e" : "winget install --id GitHub.cli -e";
                case RuntimePlatform.LinuxEditor:
                    return tool == ToolKind.Git ? "sudo apt-get install -y git" : "sudo apt-get install -y gh";
                default:
                    return string.Empty;
            }
        }

        private static bool TryInstall(ToolKind tool, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return TryInstallWithBrew(tool, out output, out error);
                case RuntimePlatform.WindowsEditor:
                    return TryInstallWithWinget(tool, out output, out error);
                case RuntimePlatform.LinuxEditor:
                    return TryInstallWithApt(tool, out output, out error);
                default:
                    error = "Unsupported platform for automatic installation.";
                    return false;
            }
        }

        internal static bool IsBrewAvailable()
        {
            return CliCommandRunner.IsCommandAvailable("brew");
        }

        internal static bool TryInstallBrew(out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            var result = CliCommandRunner.Run("bash", "-c \"/bin/bash -c \\\"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\\\"\"", GitUtility.ProjectRoot);
            output = result.StdOut;
            error = result.StdErr;
            return result.IsSuccess;
        }

        private static bool TryInstallWithBrew(ToolKind tool, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            if (!CliCommandRunner.IsCommandAvailable("brew"))
            {
                error = "Homebrew is not installed. Install Homebrew first, then try again.";
                return false;
            }

            string packageName = tool == ToolKind.Git ? "git" : "gh";
            var result = CliCommandRunner.Run("brew", $"install {packageName}", GitUtility.ProjectRoot);
            output = result.StdOut;
            error = result.StdErr;
            return result.IsSuccess;
        }

        private static bool TryInstallWithWinget(ToolKind tool, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            if (!CliCommandRunner.IsCommandAvailable("winget"))
            {
                error = "winget was not found. Install it from the Microsoft Store and try again.";
                return false;
            }

            string packageId = tool == ToolKind.Git ? "Git.Git" : "GitHub.cli";
            var result = CliCommandRunner.Run("winget", $"install --id {packageId} -e", GitUtility.ProjectRoot);
            output = result.StdOut;
            error = result.StdErr;
            return result.IsSuccess;
        }

        private static bool TryInstallWithApt(ToolKind tool, out string output, out string error)
        {
            output = string.Empty;
            error = string.Empty;

            if (!CliCommandRunner.IsCommandAvailable("apt-get"))
            {
                error = "apt-get was not found. Install the tool using your distribution's package manager.";
                return false;
            }

            string packageName = tool == ToolKind.Git ? "git" : "gh";
            var result = CliCommandRunner.Run("bash", $"-lc \"sudo -n apt-get update && sudo -n apt-get install -y {packageName}\"", GitUtility.ProjectRoot);
            output = result.StdOut;
            error = result.StdErr;
            return result.IsSuccess;
        }
    }

    internal enum ToolKind
    {
        Git,
        GitHubCli
    }
}
