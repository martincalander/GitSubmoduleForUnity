# User Guide

Open the window from **Window > Package Management > Git Package Manager**.

## Dependency Status

Git is required. If it is unavailable, the window blocks package mutations and
shows an official download page plus a platform-specific install command.

GitHub CLI is optional. Without it, **In Project** and direct URL installation
continue to work; only the **GitHub** discovery view is unavailable.

## In Project

The **In Project** tab reads `.gitmodules` and displays direct submodules whose
paths match `Packages/com.author.package`.

For each package the details panel shows:

- package name and path;
- repository URL;
- tracked branch, when configured;
- pinned commit;
- initialized or uninitialized state;
- whether a root `package.json` is present.

### Initialize or update

Select a package and choose **Initialize** when its worktree is absent, or
**Update** when it is already initialized. The operation uses the configured
remote branch and reports errors without prompting for credentials inside
Unity.

Updating changes the submodule worktree. The parent repository must still stage
and commit the resulting submodule commit.

### Change branch

Open the branch control to lazily fetch remote branch names. Choose a branch and
apply it. The tool updates `.gitmodules` using `git submodule set-branch` and
offers to update the worktree.

### Remove

Removal is confirmed before the tool deinitializes the submodule and removes its
tracked package path. Review the parent repository's staged changes before
committing.

## GitHub Discovery

The **GitHub** tab uses the authenticated `gh` session.

1. Choose the current user or a visible organization.
2. Search by repository name or description.
3. Move through results 50 repositories at a time.
4. Filter public or private repositories and sort by name or update time.
5. Select a repository to validate its root `package.json`.
6. Confirm the suggested package name and branch, then add it.

Validation is deliberately lazy: listing a page does not make a separate API
request for every repository.

## Add from URL

Use the **+** menu when GitHub CLI is unavailable or the repository is hosted
elsewhere.

Provide:

- a Git-compatible repository URL or local path;
- an optional valid branch name;
- the exact package name declared by the repository's root `package.json`.

The destination is always `Packages/<package-name>`. If the clone succeeds but
the package is missing or declares a different name, the operation is rolled
back.

## Private Repositories

Private repositories rely on the user's existing Git credential manager and,
for discovery, GitHub CLI authentication. Git Package Manager never stores
tokens, passwords, SSH keys, or credential-helper output.

Every teammate and build machine must independently have access to each private
submodule repository.
