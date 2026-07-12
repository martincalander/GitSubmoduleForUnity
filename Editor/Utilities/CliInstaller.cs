using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    internal static class CliInstaller
    {
        internal static string GetInstallHint(ToolKind tool)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return tool == ToolKind.Git ? "xcode-select --install" : "brew install gh";
                case RuntimePlatform.WindowsEditor:
                    return tool == ToolKind.Git
                        ? "winget install --id Git.Git -e"
                        : "winget install --id GitHub.cli -e";
                case RuntimePlatform.LinuxEditor:
                    return tool == ToolKind.Git
                        ? "sudo apt install git"
                        : "See https://github.com/cli/cli/blob/trunk/docs/install_linux.md";
                default:
                    return string.Empty;
            }
        }

        internal static string GetInstallUrl(ToolKind tool)
        {
            return tool == ToolKind.Git
                ? "https://git-scm.com/downloads"
                : "https://cli.github.com/";
        }
    }

    internal enum ToolKind
    {
        Git,
        GitHubCli
    }
}
