using System;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal sealed class CliInstallPlan
    {
        internal string RequestedFileName = string.Empty;
        internal string FileName = string.Empty;
        internal string ResolvedExecutablePath = string.Empty;
        internal string Arguments = string.Empty;
        internal string DisplayCommand = string.Empty;
        internal string InstallUrl = string.Empty;
        internal string AutomaticInstallUnavailableReason = string.Empty;
        internal string ExecutableLocationNotice = string.Empty;
        internal bool CanRunAutomatically;
        internal bool CanCopyCommand;
        internal bool OpensSystemInstaller;
        internal bool IsExecutableFromKnownPlatformLocation;
    }

    internal delegate bool CliExecutableResolver(string fileName, out string resolvedPath);

    internal static class CliInstaller
    {
        internal static CliInstallPlan GetInstallPlan(ToolKind tool)
        {
            return GetInstallPlanCore(
                tool,
                Application.platform,
                delegate(string fileName, out string resolvedPath)
                {
                    return CliCommandRunner.TryResolveCommand(fileName, out resolvedPath);
                });
        }

        internal static CliInstallPlan GetInstallPlan(
            ToolKind tool,
            RuntimePlatform platform,
            Func<string, bool> isCommandAvailable)
        {
            if (isCommandAvailable == null)
                throw new ArgumentNullException(nameof(isCommandAvailable));

            return GetInstallPlanCore(
                tool,
                platform,
                delegate(string fileName, out string resolvedPath)
                {
                    bool available = isCommandAvailable(fileName);
                    resolvedPath = available ? fileName : string.Empty;
                    return available;
                });
        }

        private static CliInstallPlan GetInstallPlanCore(
            ToolKind tool,
            RuntimePlatform platform,
            CliExecutableResolver resolveExecutable)
        {
            if (resolveExecutable == null)
                throw new ArgumentNullException(nameof(resolveExecutable));

            var plan = new CliInstallPlan
            {
                InstallUrl = GetInstallUrl(tool)
            };

            switch (platform)
            {
                case RuntimePlatform.OSXEditor:
                    if (tool == ToolKind.Git)
                    {
                        ConfigureCommand(plan, "xcode-select", "--install", "xcode-select --install");
                        plan.CanRunAutomatically = ResolveAndPinExecutable(plan, resolveExecutable);
                        plan.OpensSystemInstaller = true;
                        if (!plan.CanRunAutomatically)
                            plan.AutomaticInstallUnavailableReason = "The macOS command-line tools installer could not be found.";
                    }
                    else
                    {
                        ConfigureCommand(plan, "brew", "install gh", "brew install gh");
                        plan.CanRunAutomatically = ResolveAndPinExecutable(plan, resolveExecutable);
                        if (!plan.CanRunAutomatically)
                            plan.AutomaticInstallUnavailableReason = "Homebrew was not found. Use the official GitHub CLI install guide instead.";
                    }
                    break;

                case RuntimePlatform.WindowsEditor:
                    string packageId = tool == ToolKind.Git ? "Git.Git" : "GitHub.cli";
                    string wingetArguments =
                        $"install --id {packageId} -e --source winget --accept-source-agreements --accept-package-agreements";
                    ConfigureCommand(
                        plan,
                        "winget",
                        wingetArguments,
                        $"winget {wingetArguments}");
                    plan.CanRunAutomatically = ResolveAndPinExecutable(plan, resolveExecutable);
                    if (!plan.CanRunAutomatically)
                        plan.AutomaticInstallUnavailableReason = "Windows Package Manager (winget) was not found. Use the official download page instead.";
                    break;

                case RuntimePlatform.LinuxEditor:
                    if (tool == ToolKind.Git)
                    {
                        plan.DisplayCommand = GetLinuxGitInstallHint(
                            command => resolveExecutable(command, out _));
                        plan.CanCopyCommand = !string.IsNullOrWhiteSpace(plan.DisplayCommand);
                    }
                    plan.AutomaticInstallUnavailableReason =
                        "Linux installation must run in a terminal so your package manager can request administrator permission.";
                    break;

                default:
                    plan.AutomaticInstallUnavailableReason = "Automatic installation is not available on this editor platform.";
                    break;
            }

            return plan;
        }

        internal static string GetInstallUrl(ToolKind tool)
        {
            return tool == ToolKind.Git
                ? "https://git-scm.com/downloads"
                : "https://cli.github.com/";
        }

        private static void ConfigureCommand(CliInstallPlan plan, string fileName, string arguments, string displayCommand)
        {
            plan.RequestedFileName = fileName;
            plan.FileName = fileName;
            plan.Arguments = arguments;
            plan.DisplayCommand = displayCommand;
            plan.CanCopyCommand = true;
        }

        private static bool ResolveAndPinExecutable(
            CliInstallPlan plan,
            CliExecutableResolver resolveExecutable)
        {
            if (!resolveExecutable(plan.RequestedFileName, out string resolvedPath) ||
                string.IsNullOrWhiteSpace(resolvedPath))
            {
                return false;
            }

            plan.ResolvedExecutablePath = resolvedPath;
            plan.FileName = resolvedPath;
            plan.IsExecutableFromKnownPlatformLocation =
                ProcessCommandRunner.IsKnownPlatformExecutablePath(resolvedPath);
            plan.DisplayCommand = FormatDisplayCommand(resolvedPath, plan.Arguments);
            plan.ExecutableLocationNotice = plan.IsExecutableFromKnownPlatformLocation
                ? $"Executable: {resolvedPath}"
                : $"Executable resolved from PATH: {resolvedPath}. Verify this location before approving installation.";
            return true;
        }

        private static string FormatDisplayCommand(string executablePath, string arguments)
        {
            string displayPath = executablePath.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
                ? $"\"{executablePath.Replace("\"", "\\\"")}\""
                : executablePath;
            return string.IsNullOrWhiteSpace(arguments)
                ? displayPath
                : $"{displayPath} {arguments}";
        }

        private static string GetLinuxGitInstallHint(Func<string, bool> isCommandAvailable)
        {
            if (isCommandAvailable("apt-get"))
                return "sudo apt-get install git";
            if (isCommandAvailable("dnf"))
                return "sudo dnf install git";
            if (isCommandAvailable("zypper"))
                return "sudo zypper install git";
            if (isCommandAvailable("pacman"))
                return "sudo pacman -S git";

            return string.Empty;
        }
    }

    internal enum ToolKind
    {
        Git,
        GitHubCli
    }
}
