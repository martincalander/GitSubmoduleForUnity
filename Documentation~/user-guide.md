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

The checks run in the background. If a tool is missing, follow the link to its
official installation guide, install it outside Unity, and choose **Check
Again**. If GitHub CLI is unauthenticated, copy the displayed command and run it
in a visible terminal. Git Submodule Manager never accepts or stores tokens,
passwords, SSH keys, or credential-helper output.

The same setup cards appear under **Preferences > Git Submodule Manager**.
Installed tools show a checkmark and their detected version. Results refresh
automatically after a short interval; use **Check Again** for an immediate
refresh after changing a tool or its authentication.

Choose **Open GitHub Package Manager** to continue into Package Manager. Reopen
the window at any time from **Preferences > Git Submodule Manager > Show
Welcome**.

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
has no existing filter selection. The install-mode default is applied when a
discovered repository is selected; the two **Install** menu entries remain
unchecked actions. The page also provides **Open GitHub Package Manager**, setup
status, version details, installation guidance, and a manual **Check Again**
action.

Two opt-in workflow settings are available:

- **Skip Routine Confirmation** skips the second prompt only after Git verifies
  that a submodule removal or conversion is clean and routine. Warnings about
  uncommitted, unpushed, changed, or unverified work are never suppressible.
- **Install Dependencies Automatically** skips the missing-dependency prompt
  only when every missing dependency has exactly one unambiguous source.
  Ambiguous or unresolved dependencies still stop installation.

Both are off by default, so routine removals, conversions, and dependency plans
continue to ask for confirmation until you change these settings.

On first use after the package rename, an existing
`UserSettings/GitPackageManagerSettings.asset` file is copied to the new path
without deleting the original.

## GitHub Discovery and Filters

Entering **Sources > GitHub** starts the authenticated catalogue scan and shows
Package Manager's native **Refreshing list...** state and spinner. Discovery
checks repositories owned by the current GitHub user and their visible
organizations. To appear in the catalogue, a repository needs a root
`package.json` with a valid reverse-domain name and SemVer 2.0 version, plus a
regular root `package.json.meta` from the same default-branch commit. The meta
file must contain `fileFormatVersion: 2` and one nonzero 32-character hexadecimal
GUID. This helps distinguish Unity packages from ordinary npm repositories, but
it does not make a repository trustworthy or official.

Results appear incrementally and are grouped under **Organization - _owner_**.
Installed GitHub packages remain in the list throughout the scan. Choose Package
Manager's **Refresh** action to rescan the project and GitHub. If a scan is
already active, one replacement scan runs afterward.

Use Unity's native **Filters** control in the Package Manager toolbar to narrow
the page by:

- status: any package or **Downloaded** packages installed in this project;
- repository visibility: all, public, or private;
- organization: all owners or one **Organization - _owner_** value.

**Downloaded** includes both Git submodules and read-only UPM Git dependencies.
Search, sorting, and all filters remain Package Manager-native. Results from an
older scan cannot overwrite a newer refresh.

If GitHub CLI is missing or unauthenticated, remote discovery cannot start.
Installed GitHub packages remain available in the native page, and direct URL
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

Before changing the project, Git resolves the selected branch to a commit and
reads `package.json` and `package.json.meta` from that commit. After installation,
the manager rechecks the package name, version, dependencies, origin, revision,
and Git registration. Catalogue installs also retain the verified meta GUID.
Read-only installs are pinned to the inspected commit; submodule installs are
registered at `Packages/<package-name>`.

If project or repository state changes during installation, the operation stops.
The manager removes only state it can prove it created; otherwise it leaves the
state in place and tells you what to inspect. In-progress installs survive
assembly reload without repeating a completed mutation. See the
[architecture and safety model](architecture.md) for the underlying Git
postconditions and reload handoff.

## Missing Dependencies

Before installing the root package, the manager checks which declared
dependencies are still missing at the required version. Unity and configured
registry packages remain normal transitive Package Manager dependencies. A
dependency with one matching GitHub source is installed explicitly, leaf-first,
in the same mode as the root package.

`com.unity.*` requirements are searched in configured registries. Other package
names are checked on GitHub first, across the user's repositories and every
visible organization. Registry fallback is available only after a complete scan
finds no GitHub match. An incomplete scan, inaccessible manifest, duplicate
match, or version or metadata mismatch stops installation instead of guessing.

When dependencies are missing, the confirmation lists each requirement and its
resolved source. Choose **Install Dependencies & Continue** only after reviewing
that plan. If any requirement has no source or multiple possible sources, the
install stops with **Missing Dependencies Need Attention** rather than choosing
one automatically. Version-mismatched sources are also blocking.

The Preferences option to install dependencies automatically applies only when
every missing dependency has one resolved source. It never bypasses an
unresolved or ambiguous result.

## Install a Submodule from a URL

From any Package Manager page, choose **+ > Install package as Git
Submodule...**. This workflow needs Git but not GitHub CLI.

Enter a secure HTTPS or SSH repository URL, or an explicit local path. **Package
Name** and **Branch** remain disabled while Git inspects the remote. A successful
probe reads the exact name from the root `package.json`, checks the sibling
`package.json.meta`, selects the remote default branch, and adds available
branches to the branch menu.

Before cloning, review the inline trust confirmation with the redacted
repository URL, selected revision, and exact `Packages/<package-name>`
destination. Unity packages can execute Editor code, so install only a
repository you trust.

If the selected branch has a valid UPM manifest but its root
`package.json.meta` is missing or invalid, the direct installer remains
available because you supplied the repository explicitly. Its confirmation
includes a mandatory warning that the repository could not be identified
automatically as a Unity package. This exception does not add the repository to
the GitHub catalogue.

Plaintext `http://` and `git://` transports are rejected. HTTPS, `ssh://`,
SCP-style SSH addresses, `file://`, and explicit local paths are supported.
Embedded passwords and access tokens are rejected; use Git's credential manager
or an SSH agent.

Before installation completes, the checked-out manifest must match the name,
version, and dependencies found by the branch probe. If the probe verified a
Unity meta GUID, the checked-out meta file must match it too. A mismatch stops
the operation; the manager rolls back only state it can tie to that operation.

## Convert Package Source

Select an installed package and open Unity's native **Manage** menu:

- A verified editable package submodule provides **Convert to Read-Only
  Package**. The normal UPM Git dependency is recorded before the verified
  submodule worktree is removed.
- An eligible direct read-only Git dependency provides **Convert to
  Submodule**. Its `package.json` must be at the repository root; Git URLs that
  select a package in a repository subdirectory cannot be converted.

Conversion is target-first: the replacement is created and verified before the
original source is removed. Read-only-to-submodule conversion checks the root
manifest at the commit Unity resolved. Submodule-to-read-only conversion pins
the current committed revision after validating its root manifest and Unity meta
file. Missing, linked, malformed, oversized, or mismatched committed files stop
conversion before the original package changes.

The manager also checks the package and parent repository for local work.
Routine confirmation can be disabled in Preferences, but dirty or unverified
state always requires attention. See the
[architecture and safety model](architecture.md) for the immutable Git checks.

## Uninstall a Submodule

For a verified installed submodule, choose **Manage > Uninstall Submodule**.
Before making changes, the manager verifies the package path, gitlink,
`.gitmodules` registration, origin, and local state. It then stages the precise
parent-repository change instead of asking Unity to delete the package directory
directly.

The removed worktree and original `.gitmodules` file are moved to
`Library/GitSubmoduleManager/Recovery`. The completion message lists their
locations so you can inspect and delete them later. If repository state changes
during removal, the manager stops and preserves the unexpected state for
recovery.

Modified, untracked, ignored, conflicted, staged, unpushed, local-only, or
otherwise ambiguous work is never silently discarded. Review every warning and
the parent repository's staged result before committing. Git metadata is kept
when possible so the submodule can be inspected or safely added again. The
[architecture and safety model](architecture.md) describes the index and
recovery checks in detail.

## Private Repositories

Private repository cloning relies on the user's existing Git credential manager
or SSH agent. Authenticated discovery relies on GitHub CLI. Every teammate and
build machine must independently have access to each private repository.
