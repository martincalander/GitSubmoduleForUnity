# User Guide

Git Submodule Manager extends Unity's existing Package Manager. On a validated
Editor target—exact Unity `6000.3.22f1` or a Unity `6000.5.*f1` final
release—open **Window > Package Management > Package Manager**, then select
**GitHub** under **Sources**. There is no separate package management window.

The GitHub source uses Unity's native package list, search, sorting, filters,
selection, details tabs, action toolbar, and loading state. It combines
installed GitHub packages with valid root UPM packages discovered from the
authenticated user's repositories and visible organizations.

Installed package submodules are labelled **Submodule** instead of **Custom** on
normal Package Manager pages. Inside **Sources > GitHub**, repositories carry
their **Public** or **Private** visibility badge. A GitHub-hosted installed
package reports **GitHub** with the Git icon in its Source card; other supported
Git hosts report **Git**.

## Welcome and Setup

The first activation of **Sources > GitHub** shows a small standalone Welcome
window once per user and project. It checks:

- Git, which is required for installation, conversion, and removal;
- GitHub CLI, which is optional for direct URL installation but required for
  authenticated discovery;
- GitHub CLI authentication for `github.com`.

The checks run away from Unity's UI thread. If a tool is missing, use the link
to its official installation guidance, install it outside Unity, and choose
**Check Again**. If GitHub CLI is unauthenticated, copy the displayed
authentication command, complete it in a visible terminal, and check again.
Git Submodule Manager never accepts or stores tokens, passwords, SSH keys, or
credential-helper output.

The same live setup cards appear under **Preferences > Git Submodule Manager**.
Installed tools show a checkmark and their detected version. Preferences and
Welcome share one probe, so opening both does not start competing GitHub CLI
authentication checks. A cached result refreshes automatically after a short
freshness window; use **Check Again** for an immediate refresh after changing a
tool or its authentication.

Choose **Open GitHub Package Manager** to continue into Package Manager. Reopen the
window at any time from **Preferences > Git Submodule Manager > Show Welcome**.

## Preferences

Open **Edit > Preferences > Git Submodule Manager** on Windows and Linux, or
Unity's **Settings > Git Submodule Manager** page on macOS. Settings are stored
per user and project in
`UserSettings/GitSubmoduleManagerSettings.asset`; they do not modify the package
or create team-shared project settings.

Package Manager defaults include:

- **Visibility**: all repositories (default), public only, or private only;
- **Organization**: a GitHub organization login, or blank by default for all
  owners;
- **Install Mode**: **Git Submodule** (default) or **Read-Only Package**.

Visibility and organization defaults are applied only when **Sources > GitHub**
has no existing filter selection. The install-mode default initializes the
selector for a discovered repository. The page also provides **Open GitHub
Package Manager**, setup status, version details, installation guidance, and a
manual **Check Again** action.

Two opt-in workflow settings are available:

- **Skip Routine Confirmation** skips the second prompt only after Git verifies
  that a submodule removal or conversion is clean and routine. Warnings about
  uncommitted, unpushed, changed, or unverified work are never suppressible.
- **Install Dependencies Automatically** skips the missing-dependency prompt
  only when every missing dependency has exactly one safely resolved source.
  Ambiguous or unresolved dependencies still stop installation.

Both are off by default, so routine removal/conversion and every non-empty safe
dependency plan continue to ask for confirmation until explicitly changed.

On first use after the package rename, an existing
`UserSettings/GitPackageManagerSettings.asset` file is copied to the new path
without deleting the original.

## GitHub Discovery and Filters

Entering **Sources > GitHub** starts the authenticated catalogue scan and shows
Package Manager's native **Refreshing list...** state and spinner while work is
in progress. Discovery walks repository pages for the current GitHub user and
their visible organizations. Root `package.json` files are checked in bounded
batches, and only manifests with a valid reverse-domain UPM package name and
SemVer 2.0 version enter the catalogue.

Results appear incrementally and are grouped under **Organization - _owner_**.
Installed GitHub packages remain in the same list throughout the scan. Two
organizations can load concurrently, while pages within each organization stay
ordered. Choose Package Manager's **Refresh** action to rescan the project and
GitHub; if a scan is active, one replacement refresh starts after its bounded
GitHub reads finish.

Use Unity's native **Filters** control in the Package Manager toolbar to narrow
the page by:

- status: any package or **Downloaded** packages installed in this project;
- repository visibility: all, public, or private;
- organization: all owners or one **Organization - _owner_** value.

**Downloaded** includes both Git submodules and read-only UPM Git dependencies.
Search, sorting, and all filters remain Package Manager-native. Discovery keeps
only the current scan generation so stale owner, page, or refresh results cannot
overwrite the newest state.

If GitHub CLI is missing or unauthenticated, remote discovery cannot start.
Installed GitHub submodules remain available in the native page, and direct URL
installation continues to require only Git.

## Install a Discovered Package

Select an uninstalled result in **Sources > GitHub**:

1. Review its name, owner, version, description, Source card, and **Repository**
   link.
2. Choose a branch. `main` is selected when it exists; otherwise the remote
   default branch is selected. Git loads other remote branches on demand.
3. Open **Install** and choose **Install as Git Submodule** for an editable
   checkout under `Packages/`, or **Install as Read-Only Package** for a normal
   UPM Git dependency.
4. Review the repository, revision, install mode, and any dependency plan before
   confirming.

The submodule mode clones to `Packages/<package-name>` and validates the root
manifest, Git registration, origin, branch, and package identity. The read-only
mode asks Unity Package Manager to add an exact direct Git manifest entry. In
both modes, the installed root `package.json` must retain the exact package name,
version, and dependency map inspected before installation. A mismatched new
install is rolled back or removed only when termination and cleanup ownership
can be proven; otherwise the package warns that the checkout or
`Packages/manifest.json` entry may remain and gives inspection instructions.

Once mutation begins, the ordered install and its current step survive assembly
reload. An in-flight step is not started twice: the coordinator resumes from
Unity's registered package state and retains the final success or failure until
Package Manager can present it once.

## Missing Dependencies

Before the root package is installed, its declared dependencies are compared
with packages already registered directly or transitively and with sources that
can be resolved safely. Only missing dependencies enter the plan. Unity/default
and configured-registry dependencies remain normal transitive Package Manager
dependencies and are never added directly. Uniquely matched GitHub dependencies
are installed explicitly, leaf-first, in the root package's chosen mode.

An installed package counts as satisfied only when its complete identity and
exact version match. `com.unity.*` requirements use registry search directly.
For every other name, GitHub has priority: resolution waits for all personal and
visible-organization owners to finish successfully, uses a unique exact GitHub
match when present, and consults configured registries only after that complete
scan proves the package absent. An incomplete scan, owner-coverage warning,
unavailable manifest, duplicate GitHub match, or GitHub version/metadata mismatch
blocks the root install instead of falling through to a registry.

When dependencies are missing, the confirmation lists each requirement and its
resolved source. Choose **Install Dependencies & Continue** only after reviewing
that plan. If any requirement has no source or multiple possible sources, the
install stops with **Missing Dependencies Need Attention** rather than choosing
one automatically. Version-mismatched sources are also blocking.

The Preferences option to install dependencies automatically applies only to a
completed plan in which every missing dependency has one resolved candidate. It
does not bypass unresolved or ambiguous results.

## Install a Submodule from a URL

From any Package Manager page, choose **+ > Install package as Git
Submodule...**. This workflow needs Git but not GitHub CLI.

Enter a secure HTTPS or SSH repository URL, or an explicit local path. **Package
Name** and **Branch** remain disabled while Git inspects the remote. A successful
probe reads the exact name from the root `package.json`, selects the remote
default branch, and adds available branches to the branch menu.

Before cloning, review the inline trust confirmation with the redacted
repository URL, selected revision, and exact `Packages/<package-name>`
destination. Unity packages can execute Editor code, so install only a
repository you trust.

Plaintext `http://` and `git://` transports are rejected. HTTPS, `ssh://`,
SCP-style SSH addresses, `file://`, and explicit local paths are supported.
Embedded passwords and access tokens are rejected; use Git's credential manager
or an SSH agent.

The cloned root manifest must retain the exact name, version, and dependency map
read by the branch probe. Missing or mismatched manifests and failed Git
postconditions trigger safe rollback when ownership can be proven.

## Convert Package Source

Select an installed package and open Unity's native **Manage** menu:

- A verified editable package submodule provides **Convert to Read-Only
  Package**. The normal UPM Git dependency is recorded before the verified
  submodule worktree is removed.
- An eligible direct read-only Git dependency provides **Convert to
  Submodule**. Its `package.json` must be at the repository root; Git URLs that
  select a package in a repository subdirectory cannot be converted.

Read-only-to-submodule conversion creates and verifies the submodule before
removing the manifest dependency. Submodule-to-read-only conversion pins the
current committed revision. Conversion inspects local and parent-repository
state first. Routine confirmation can be disabled in Preferences, but dirty or
unverified-state warnings always require attention.

## Uninstall a Submodule

For a verified installed submodule, choose **Manage > Uninstall Submodule**.
Git Submodule Manager uses Git's tracked removal instead of Unity's raw embedded
package-directory deletion. It verifies the package path, gitlink,
`.gitmodules` registration, worktree origin, and local state before mutation.

Modified, untracked, ignored, conflicted, staged, unpushed, local-only, or
otherwise ambiguous work is never silently discarded. Review every warning and
the parent repository's staged result before committing. Git object metadata is
retained when possible for recovery and a safe re-add.

## Private Repositories

Private repository cloning relies on the user's existing Git credential
manager. Authenticated discovery relies on GitHub CLI. Every teammate and build
machine must independently have access to each private repository.
