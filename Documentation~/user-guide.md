# User Guide

Open the window from **Window > Package Management > Git Submodule Manager**.

## Welcome and Setup

The first open shows a Unity-native setup page with the permanent menu path and
live status for Git, GitHub CLI, and GitHub authentication. Git is required to
continue. GitHub CLI is recommended but optional, so missing GitHub setup never
blocks **In Project** or direct URL installation.

Install buttons reuse the normal explicit-permission flow: the exact native
command is shown before it can run. If GitHub CLI is installed but not
authenticated, choose **Authenticate with GitHub...**. Unity opens GitHub's
device page and GitHub CLI copies its one-time device code to the clipboard. If
no code is available, cancel and run the displayed command in a visible
terminal after cancellation finishes. If Unity cannot confirm that the process
stopped, restart Unity before retrying. Git Submodule Manager never accepts or
stores a token, and discovery is enabled only after a fresh active-account
authentication-status check succeeds.
One-click login requires GitHub CLI 2.79.0 or newer; older versions show the
compatible terminal command. The approval dialog discloses that the automated
flow selects HTTPS as GitHub CLI's host-wide Git protocol.

The welcome page is shown automatically once per user and project. Reopen it
at any time from **Welcome & Setup...** in the window menu.

## Preferences

Open **Edit > Preferences > Git Submodule Manager** on Windows and Linux, or
**Unity > Settings > Git Submodule Manager** on macOS. The user preferences let
you choose the startup tab, the initial GitHub repository filter, and whether
**In Project** refreshes when revisited after a configurable interval.

Choose **Open Welcome & Setup** to run the dependency and authentication setup
again. Settings are stored per user and project in
`UserSettings/GitSubmoduleManagerSettings.asset`; they do not modify the package
or create team-shared project settings. On first use after the rename, an
existing `UserSettings/GitPackageManagerSettings.asset` file is copied to the
new path without deleting the original.

## Dependency Status

Git is required. If it is unavailable, the window blocks package mutations and
shows an official download page plus a platform-specific install command. On
supported macOS and Windows setups, **Install Git...** shows the exact command
and asks for permission before running it. Linux keeps the administrator prompt
in a normal terminal.

GitHub CLI is optional. Without it, **In Project** and direct URL installation
continue to work; only the **GitHub** discovery view is unavailable. Its install
card uses the same explicit-permission flow when a supported native package
manager is available. An installed but unauthenticated GitHub CLI shows the
browser authentication action, terminal command fallback, and a separate
**Check again** action.

When Git is missing, package lists, tabs, and mutation controls are locked
behind the Git dependency card. When only GitHub CLI is missing or not
authenticated, **In Project** and **+ > Add Submodule...** remain available and
only the **GitHub** tab is blocked.

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
4. Filter public or private repositories, or choose **Valid UPM Packages** to
   show only repositories with a valid root manifest.
5. Select a repository to inspect its root `package.json` validation result.
6. Confirm the suggested package name and branch, then add it.

Validation is deliberately lazy. A selected repository is checked on demand;
the **Valid UPM Packages** filter checks only the current 50-item page and uses
small GitHub GraphQL batches rather than starting one process per repository.
A valid manifest must be a JSON object with a reverse-domain UPM package name
and a SemVer 2.0 `version`.

## Add from URL

Use the **+** menu when GitHub CLI is unavailable or the repository is hosted
elsewhere.

Provide:

- a secure HTTPS or SSH repository URL, or an explicit local path;
- an optional valid branch name;
- the exact package name declared by the repository's root `package.json`.

Leave the branch empty to use the repository's default branch. This manual
workflow uses Git directly and never requires GitHub CLI, including for GitHub
repository URLs.

Plaintext `http://` and `git://` repository transports are intentionally
blocked because they do not protect the package source from substitution in
transit. HTTPS, `ssh://`, SCP-style SSH addresses such as
`git@example.com:owner/repository.git`, `file://`, and explicit local paths are
supported. Embedded passwords and access tokens are rejected; use Git's normal
credential manager or SSH agent instead.

The destination is always `Packages/<package-name>`. If the clone succeeds but
the package is missing or declares a different name, the operation is rolled
back.

## Private Repositories

Private repositories rely on the user's existing Git credential manager and,
for discovery, GitHub CLI authentication. Git Submodule Manager never stores
tokens, passwords, SSH keys, or credential-helper output.

Every teammate and build machine must independently have access to each private
submodule repository.
