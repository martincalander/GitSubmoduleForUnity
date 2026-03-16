using System;
using System.Collections.Generic;

namespace GitPackageManager.Editor
{
    internal sealed class DiscoveryCoordinator
    {
        private const int PageSize = 30;
        private const double SearchDebounceSeconds = 0.3;

        private string cachedUsername = string.Empty;
        private AsyncCommandHandle usernameHandle;
        private AsyncCommandHandle orgsHandle;

        private AsyncCommandHandle pageHandle;
        private int currentPage = 1;
        private string currentSearchQuery = string.Empty;
        private bool isSearchMode;
        private string selectedOwner = string.Empty;

        private double pendingSearchTime;
        private string pendingSearchQuery;

        private AsyncCommandHandle packageJsonHandle;
        private GitHubRepo packageJsonTarget;

        internal bool IsLoading => pageHandle != null && !pageHandle.IsComplete;
        internal int CurrentPage => currentPage;
        internal string StatusMessage => pageHandle?.StatusMessage ?? string.Empty;

        internal List<GitHubRepo> DisplayedRepos { get; private set; } = new();
        internal bool HasResults => DisplayedRepos.Count > 0;
        internal bool HasNextPage { get; private set; }
        internal bool HasPrevPage => currentPage > 1;
        internal bool PageChanged { get; private set; }

        internal string Username => cachedUsername;
        internal string SelectedOwner => selectedOwner;
        internal List<string> Organizations { get; private set; } = new();
        internal bool OrgsLoaded { get; private set; }

        internal void EnsureUsername()
        {
            if (!string.IsNullOrEmpty(cachedUsername) || usernameHandle != null)
            {
                return;
            }

            usernameHandle = CliCommandRunner.RunAsync("gh", "api user --jq .login", GitUtility.ProjectRoot);
        }

        internal void SetOwner(string owner)
        {
            if (string.Equals(selectedOwner, owner, StringComparison.OrdinalIgnoreCase))
                return;

            selectedOwner = owner ?? string.Empty;
            LoadInitialPage();
        }

        internal void LoadInitialPage()
        {
            currentPage = 1;
            isSearchMode = false;
            currentSearchQuery = string.Empty;
            FetchPage();
        }

        internal void SetSearchQuery(string query, double currentTime)
        {
            if (string.Equals(query, pendingSearchQuery, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pendingSearchQuery = query;
            pendingSearchTime = currentTime + SearchDebounceSeconds;
        }

        internal void NextPage()
        {
            if (!HasNextPage)
            {
                return;
            }

            currentPage++;
            FetchPage();
        }

        internal void PrevPage()
        {
            if (!HasPrevPage)
            {
                return;
            }

            currentPage--;
            FetchPage();
        }

        internal void CheckPackageJson(GitHubRepo repo)
        {
            if (repo == null || repo.PackageJsonChecked)
                return;

            if (packageJsonTarget == repo && packageJsonHandle != null)
                return;

            packageJsonTarget = repo;
            packageJsonHandle = CliCommandRunner.RunAsync("gh",
                $"api repos/{repo.Owner}/{repo.Name}/contents/package.json",
                GitUtility.ProjectRoot);
        }

        internal bool Tick(double currentTime)
        {
            bool changed = false;
            PageChanged = false;

            if (usernameHandle != null && usernameHandle.IsComplete)
            {
                if (usernameHandle.Result.IsSuccess)
                {
                    cachedUsername = usernameHandle.Result.StdOut.Trim();
                    if (string.IsNullOrEmpty(selectedOwner))
                        selectedOwner = cachedUsername;
                }

                usernameHandle = null;

                if (!OrgsLoaded && orgsHandle == null)
                {
                    orgsHandle = CliCommandRunner.RunAsync("gh",
                        "api user/orgs --jq \".[].login\"", GitUtility.ProjectRoot);
                }
            }

            if (orgsHandle != null && orgsHandle.IsComplete)
            {
                if (orgsHandle.Result.IsSuccess)
                {
                    Organizations = new List<string>();
                    string output = orgsHandle.Result.StdOut.Trim();
                    if (!string.IsNullOrEmpty(output))
                    {
                        foreach (string line in output.Split('\n'))
                        {
                            string org = line.Trim();
                            if (!string.IsNullOrEmpty(org))
                                Organizations.Add(org);
                        }
                    }
                }

                OrgsLoaded = true;
                orgsHandle = null;
                changed = true;
            }

            if (pendingSearchQuery != null && currentTime >= pendingSearchTime)
            {
                string query = pendingSearchQuery;
                pendingSearchQuery = null;

                if (string.IsNullOrWhiteSpace(query))
                {
                    if (isSearchMode)
                    {
                        LoadInitialPage();
                    }
                }
                else
                {
                    currentPage = 1;
                    isSearchMode = true;
                    currentSearchQuery = query;
                    FetchPage();
                }
            }

            if (pageHandle != null && pageHandle.IsComplete)
            {
                var result = pageHandle.Result;
                pageHandle = null;

                if (result.IsSuccess)
                {
                    string json = result.StdOut.Trim();
                    List<GitHubRepo> repos;

                    if (isSearchMode)
                    {
                        repos = GitHubUtility.ParseSearchJson(json);
                    }
                    else
                    {
                        repos = GitHubUtility.ParseRepoJson(json);
                    }

                    DisplayedRepos = repos ?? new List<GitHubRepo>();
                    HasNextPage = DisplayedRepos.Count == PageSize;
                    PageChanged = true;
                    packageJsonHandle = null;
                    packageJsonTarget = null;
                }
                else
                {
                    DisplayedRepos = new List<GitHubRepo>();
                    HasNextPage = false;
                    PageChanged = true;
                }

                changed = true;
            }

            if (packageJsonHandle != null && packageJsonHandle.IsComplete)
            {
                var result = packageJsonHandle.Result;
                var target = packageJsonTarget;
                packageJsonHandle = null;
                packageJsonTarget = null;

                if (target != null)
                {
                    if (result.IsSuccess)
                    {
                        target.HasPackageJson = true;
                        target.PackageJsonChecked = true;
                    }
                    else if (GitHubUtility.IsNotFoundResult(result))
                    {
                        target.HasPackageJson = false;
                        target.PackageJsonChecked = true;
                    }
                    // else: transient error — leave unchecked so it retries on next select
                }

                changed = true;
            }

            return changed;
        }

        private void FetchPage()
        {
            string owner = selectedOwner;
            bool isOwnRepos = string.IsNullOrEmpty(owner) ||
                string.Equals(owner, cachedUsername, StringComparison.OrdinalIgnoreCase);

            string args;
            if (isSearchMode && !string.IsNullOrWhiteSpace(currentSearchQuery))
            {
                string qualifier = !string.IsNullOrEmpty(owner)
                    ? (isOwnRepos ? $"user:{owner}+" : $"org:{owner}+")
                    : string.Empty;
                string encoded = Uri.EscapeDataString(currentSearchQuery);
                args = $"api \"search/repositories?q={qualifier}{encoded}&per_page={PageSize}&page={currentPage}\"";
            }
            else if (isOwnRepos)
            {
                args = $"api user/repos?sort=updated&direction=desc&per_page={PageSize}&page={currentPage}";
            }
            else
            {
                args = $"api orgs/{owner}/repos?sort=updated&direction=desc&per_page={PageSize}&page={currentPage}";
            }

            pageHandle = CliCommandRunner.RunAsync("gh", args, GitUtility.ProjectRoot);
        }

        internal void Dispose()
        {
            pageHandle = null;
            usernameHandle = null;
            orgsHandle = null;
            packageJsonHandle = null;
            packageJsonTarget = null;
            DisplayedRepos = new List<GitHubRepo>();
            currentPage = 1;
            isSearchMode = false;
            currentSearchQuery = string.Empty;
            pendingSearchQuery = null;
        }
    }
}
