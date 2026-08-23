namespace MartinCalander.GitSubmoduleManager.Editor
{
    internal static class CliInstaller
    {
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
