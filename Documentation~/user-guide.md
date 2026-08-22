# User Guide

On Unity versions with Package Manager extension-page support, open **Window >
Package Manager** and select **GitHub** under **Sources**. This is a fully native
Package Manager page that combines installed GitHub submodules with valid UPM
packages discovered incrementally from every authenticated personal and
organization repository page. It uses Unity's package list, search, sorting,
selection, and package details. Select an uninstalled discovery result, review
its **Repository** link, select a branch (the repository default is preselected),
and choose **Install**. Review the inline trust warning, then choose **Confirm
Install**; the same details pane reports progress and any recoverable error.
Installed submodules expose **Remove Submodule** in the same native details
area. Removal uses an inline confirmation and Git's tracked removal rather than
Unity's embedded-package directory deletion.
**Refresh** rescans the project and GitHub.

Open **Window > Package Management > Git Submodule Manager** for the complete
management workspace. It contains **In Project**, authenticated GitHub
discovery, direct add, initialize, update, retarget, and remove workflows.

On older supported Unity versions without the extension-page contract, the menu
opens the management workspace embedded in Package Manager. Use **Back to
Package Manager** to return to Unity's package list.

Installed package submodules are labelled **Submodule** instead of **Custom** on
normal Package Manager pages. Inside **Sources > GitHub**, installed and
discovered repositories are labelled **Public** or **Private** because the
installed checkmark already communicates installation state. Their Source card
shows **GitHub** with the Git icon for GitHub repositories, or **Git** for other
supported Git hosts.

## Welcome and Setup

The first management-workspace open shows a Unity-native setup page with live
status for Git, GitHub CLI, and GitHub authentication. Git is required to
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

The welcome page is shown automatically once per user and project. Reopen it at
any time from **Welcome & Setup...** in the management workspace's menu.

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

Git is required. If it is unavailable, the management workspace blocks package
mutations and shows an official download page plus a platform-specific install
command. On supported macOS and Windows setups, **Install Git...** shows the
exact command and asks for permission before running it. Linux keeps the
administrator prompt in a normal terminal.

GitHub CLI is optional. Without it, **In Project** and direct URL installation
continue to work. **Sources > GitHub** still shows installed GitHub submodules,
but remote discovery is unavailable until GitHub CLI is authenticated. The
embedded management fallback remains available. Its install card uses the same
explicit-permission flow when a supported native package manager is available.
An installed but unauthenticated GitHub CLI shows the browser authentication
action, terminal command fallback, and a separate **Check again** action.

When Git is missing, package lists, tabs, and mutation controls are locked
behind the Git dependency card. When only GitHub CLI is missing or not
authenticated, **In Project** and **+ > Add Submodule...** remain available and
only authenticated remote discovery is blocked. Installed entries remain in the
native GitHub source, while the management workspace's **GitHub** tab shows its
dependency guidance.

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

Removal is confirmed before the tool runs Git's canonical tracked removal for
the submodule registration and package worktree. Modified, untracked, ignored,
conflicted, and local-only work blocks removal unless it is explicitly reviewed
in the full management workspace. Git object metadata is retained for recovery
and a safe re-add. Review the parent repository's staged changes before
committing; a committed submodule removal is expected to leave staged changes.

## GitHub Discovery

On Unity versions with extension-page support, **Sources > GitHub** starts one
authenticated catalogue scan. It walks all 50-item repository pages for the
current GitHub user and every organization returned for that account. Root
`package.json` files are validated in bounded GitHub GraphQL batches, and valid
UPM packages appear incrementally rather than waiting for the whole account to
finish. Installed GitHub submodules remain in the same native list throughout
the scan. Entering the page starts the first scan and shows Package Manager's
native **Refreshing list...** state and spinner until it settles. Results are
grouped under **Organization - _owner_** and each discovered repository is
labelled **Public** or **Private**.

Use Package Manager's native search, sorting, selection, and details controls
across the combined results. **Refresh** discards the remote scan state and
rescans GitHub and installed submodules. A discovered repository is shown only
after its root manifest declares a valid reverse-domain UPM package name and a
SemVer 2.0 `version`.

To install a discovered result:

1. Select the package and review its owner, source, version, and description.
2. Choose **Repository** to inspect the repository website when needed.
3. Choose a branch. The repository's default branch is selected before the
   Git-based remote branch list finishes loading.
4. Choose **Install** and confirm the repository, branch, and destination.
5. The package is added below `Packages/` on the selected branch.

The native action uses the same serialized Git transaction as direct addition.
It validates the cloned manifest and Git registration, then safely rolls back a
failed clone, package-name mismatch, or failed postcondition whenever process
termination and repository ownership can be proven.

The management workspace retains its interactive **GitHub** discovery tab. On
older Unity versions this embedded workflow provides discovery inside Package
Manager:

1. Choose the current user or a visible organization.
2. Search by repository name or description.
3. Move through results 50 repositories at a time.
4. Filter public or private repositories, or choose **Valid UPM Packages** to
   show only repositories with a valid root manifest.
5. Select a repository to inspect its root `package.json` validation result.
6. Confirm the suggested package name and branch, then add it.

In the embedded workflow, validation is deliberately lazy. A selected
repository is checked on demand;
the **Valid UPM Packages** filter checks only the current 50-item page and uses
small GitHub GraphQL batches rather than starting one process per repository.

## Add from URL

From any open Package Manager page, choose **+ > Install package as Git
Submodule...** when GitHub CLI is unavailable, the repository is hosted
elsewhere, or you already know its URL. The command is available wherever
Package Manager is open; you do not need to navigate to **Sources > GitHub** or
open the management workspace first.

Enter a secure HTTPS or SSH repository URL, or an explicit local path. Package
Name and Branch remain disabled while Git inspects the repository. When the
probe completes, it fills the exact name from the root `package.json`, selects
the remote default branch, and adds the available remote branches to the
branch menu. Both values remain editable for manual fallback. This workflow
uses Git directly and never requires GitHub CLI, including for GitHub
repository URLs.

The popup starts with a compact height. Status and error text expands into a
bounded scrolling area only when needed, so the action row remains reachable
without leaving a large empty lower section.

Before cloning, the popup shows an inline trust confirmation with the redacted
repository URL, chosen branch or repository default, and exact
`Packages/<package-name>` destination. Choose **Confirm Install** only for a
repository you trust, because Unity packages can execute Editor code. The same
inline status area reports installation progress and recoverable errors.

Plaintext `http://` and `git://` repository transports are intentionally
blocked because they do not protect the package source from substitution in
transit. HTTPS, `ssh://`, SCP-style SSH addresses such as
`git@example.com:owner/repository.git`, `file://`, and explicit local paths are
supported. Embedded passwords and access tokens are rejected; use Git's normal
credential manager or SSH agent instead.

The destination is always `Packages/<package-name>`. If the clone succeeds but
the root `package.json` is missing, declares a different package name, or a
required Git postcondition fails, the operation safely rolls back whenever
process termination and repository ownership can be proven. Otherwise it
reports explicit recovery instructions instead of deleting ambiguous files.

## Private Repositories

Private repositories rely on the user's existing Git credential manager and,
for discovery, GitHub CLI authentication. Git Submodule Manager never stores
tokens, passwords, SSH keys, or credential-helper output.

Every teammate and build machine must independently have access to each private
submodule repository.
