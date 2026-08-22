<p align="center">
  <a href="https://git-scm.com/" title="Git">
    <picture>
      <img src="GitSubmoduleManagerIcon.png" alt="Git" width="72" height="72">
    </picture>
  </a>
  &nbsp;&nbsp;&nbsp;&nbsp;
  <a href="https://unity.com/" title="Unity">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="https://cdn.simpleicons.org/unity/FFFFFF">
      <source media="(prefers-color-scheme: light)" srcset="https://cdn.simpleicons.org/unity/000000">
      <img src="https://cdn.simpleicons.org/unity/000000" alt="Unity" width="72" height="72">
    </picture>
  </a>
</p>

# Git Submodule Manager

Manage Git-hosted Unity packages as editable submodules or read-only
dependencies, directly from the Editor.

<p align="center">
  <a href="https://github.com/martincalander/GitSubmoduleManager/actions/workflows/ci.yml"><img alt="Sanity Checks" src="https://github.com/martincalander/GitSubmoduleManager/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/martincalander/GitSubmoduleManager/releases"><img alt="Package version" src="https://img.shields.io/github/package-json/v/martincalander/GitSubmoduleManager?filename=package.json&label=package"></a>
  <img alt="Unity 6000.5 final patch releases" src="https://img.shields.io/badge/Unity-6000.5.*f1-222C37?logo=unity&logoColor=white">
  <img alt="Editor only" src="https://img.shields.io/badge/scope-Editor%20only-555">
  <a href="LICENSE.md"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

Git Submodule Manager is an editor-only Unity package for installing and
managing Git-hosted UPM packages as editable submodules under `Packages/` or as
normal read-only Git dependencies. Unity `6000.5.*f1` releases add a native
GitHub source and Git workflows directly to Package Manager. Editable checkouts
remain completely standard and visible to normal Git tooling.

## What It Does

- Adds a native **Sources > GitHub** page to **Window > Package Management >
  Package Manager** on supported Unity `6000.5.*f1` releases. It incrementally
  discovers valid UPM packages across
  the authenticated user's repositories and organizations, combines them with
  installed GitHub submodules, and uses Unity's package list, search, sort,
  selection, and details UI.
- Adds a **Repository** website link, a branch selector that prefers `main` and
  falls back to the remote default when `main` is unavailable, an install-mode
  selector for **Git Submodule** or
  **Read-Only Package**, and a primary **Install** action to a discovered
  package's native details pane.
- Adds **+ > Install package as Git Submodule...** to Package Manager wherever
  it is open. After a repository URL is entered, a Git-only probe fills the
  root `package.json` package name and default branch and offers the remote
  branches without requiring GitHub CLI. A trust confirmation precedes the
  shared transaction; failed additions are rolled back whenever cleanup
  ownership can be proven.
- Labels installed package submodules as **Submodule** on normal Package Manager
  pages. Inside **Sources > GitHub**, installed and discovered repositories use
  their **Public** or **Private** badge instead. GitHub-hosted installed packages
  show the **GitHub** source with the Git icon.
- Routes Package Manager removal of installed submodules through guarded
  `git rm`, preserving local work and keeping the parent gitlink and
  `.gitmodules` registration consistent. The native **Manage** menu also converts
  editable submodules to read-only Git packages and eligible root read-only Git
  packages back to submodules.
- Resolves missing package dependencies before installation. A complete,
  unambiguous plan is shown for confirmation; unresolved or ambiguous sources
  stop the install instead of being guessed.
- Adds native visibility and organization filters to the GitHub source. Their
  defaults, the default install mode, dependency prompting, and routine clean
  removal/conversion confirmation are configurable per user in **Preferences >
  Git Submodule Manager**. Safe defaults show all repositories from all owners,
  install as a Git submodule, and keep both confirmation prompts active.
- Provides shared setup cards in Preferences and the standalone Welcome window.
  They show Git and GitHub CLI versions, installation and authentication
  checkmarks, official install/help actions, and open **Sources > GitHub**.
- Adds a package from a secure HTTPS or SSH repository URL, or from an explicit
  local repository path.
- Discovers repositories from GitHub users and organizations when `gh` is
  available.
- Validates that a repository contains a root UPM `package.json` before keeping
  it installed.
- Initializes, updates, retargets, and removes submodules using explicit user
  actions.
- Supports Windows, macOS, and Linux Editor environments.

It does not replace Git, store credentials, silently install command-line
tools, or manage non-UPM content and arbitrary project folders.
There is no legacy package-management window or embedded fallback; current
workflows use Unity's native Package Manager surface.

## Requirements

| Dependency | Requirement | Used for |
| --- | --- | --- |
| Unity | `6000.5.*f1` final releases | Native Package Manager integration |
| [Git CLI](https://git-scm.com/downloads) | Required | All submodule operations |
| [GitHub CLI](https://cli.github.com/) | Optional, recommended | Authenticated repository discovery |

Git must be available to the Unity Editor process. GitHub CLI is needed only
for authenticated GitHub repository discovery. The standalone Welcome window
checks both tools, links to their official installation guidance, and provides
a copyable GitHub authentication command without accepting or storing tokens.

## Installation

Open **Window > Package Management > Package Manager**, choose **+ > Install
package from git URL...**, and enter:

```text
https://github.com/martincalander/GitSubmoduleManager.git
```

Git Submodule Manager itself is installed as a UPM Git dependency. Packages
managed by the tool can be installed as editable Git submodules or normal
read-only UPM Git dependencies.

## Usage

1. On Unity `6000.5.*f1`, open **Window > Package Management > Package
   Manager** and select **GitHub** under **Sources**. Valid packages discovered
   from your personal repositories and organizations appear incrementally beside
   installed GitHub submodules in Unity's native package list and details UI.
   Organization headers, **Public**/**Private** badges, and Unity's native
   loading indicator make the catalogue state visible while it is being
   populated.
2. Use Package Manager's **Filters** control to select **All**, **Public**, or
   **Private** repositories and, when desired, a specific **Organization -
   _owner_**.
3. Select a discovered package, follow **Repository** to inspect it, choose a
   branch and install mode, and choose **Install**. Review any missing-dependency
   plan before continuing. Use **Refresh** to rescan GitHub and the project.
4. For an installed package, use Package Manager's native **Manage** menu to
   convert between eligible read-only Git packages and submodules, or to
   uninstall a submodule safely.
5. Open **Preferences > Git Submodule Manager** to choose GitHub filter and
   install defaults, change confirmation behavior, reopen **Show Welcome**, or
   jump directly to **Open GitHub Package Manager**.
6. From any open Package Manager page, choose **+ > Install package as Git
   Submodule...** to add a repository directly by URL. Git inspects the remote
   package name, default branch, and available branches before enabling those
   fields.

Every mutation begins with an explicit user action. Confirmations are shown by
default; Preferences can skip only verified routine prompts and complete,
unambiguous dependency plans. Progress, failures, validation results, and
recovery instructions are reported in the Editor.

## Repository Discovery

The native **Sources > GitHub** catalogue walks every repository page for the
authenticated user and each visible organization. It validates root
`package.json` files in bounded batches and publishes only confirmed UPM
packages as results become available. **Refresh** starts a new project and
GitHub scan.

Package Manager's native **Filters** menu can narrow the catalogue by repository
visibility and organization. The per-user Preferences page supplies the initial
visibility and organization when the page has no existing filter selection.
Search uses Package Manager's native projected-package search, stale discovery
requests are cancelled, and branch information is loaded only when requested.

Without GitHub CLI, direct Git URLs and installed-submodule management remain
available. The native GitHub source continues to show installed submodules when
remote discovery cannot start. Git is the only dependency used by direct URL
installation; `gh` is required only for authenticated repository discovery.

## Dependency Planning

Already registered packages satisfy a requirement only when Unity exposes one
complete identity at the exact requested version. `com.unity.*` requirements go
directly to configured registry search. For every other missing package, GitHub
has priority: the resolver waits for a successful scan of the authenticated
user and every visible organization, uses a unique exact GitHub match when one
exists, and searches configured registries only after that complete catalogue
proves the package absent. Discovery errors, incomplete owner coverage,
unavailable manifests, duplicate matches, and GitHub version or metadata
mismatches block installation rather than falling through to a registry.

The missing-dependency prompt identifies the exact source selected for each
requirement. GitHub dependencies install explicitly, leaf-first, in the root
package's selected mode; registry dependencies remain transitive. The automatic
preference can skip this prompt only for a complete, unambiguous plan.

## Safety Model

- Managed paths must be direct children of `Packages/` with a valid
  `com.author.package` name.
- Network repositories must use HTTPS or SSH (including SCP-style SSH URLs).
  Plaintext `http://` and `git://` transports are rejected to prevent an
  in-transit repository substitution; explicit local and `file://` repositories
  remain supported.
- Commands are executed without a shell, so repository input is not evaluated
  as shell syntax.
- Git credential prompts are disabled inside Editor processes to prevent Unity
  from hanging on hidden interactive input.
- Standard output and error are drained concurrently and operations use bounded
  timeouts.
- After a dependency-aware install, the root manifest must match the exact name,
  version, and dependency map inspected before mutation. A mismatched new
  submodule or read-only manifest entry is rolled back or removed when ownership
  is proven; otherwise the package reports that project state may remain and
  must be inspected before retrying. Manifests must be regular, bounded UTF-8
  files with a reverse-domain package name and a SemVer 2.0 version.
- Persisted `.gitmodules`, local Git configuration, and worktree origins are
  revalidated before a mutation, and incomplete structural Git output fails
  closed instead of being partially trusted.
- Network-heavy add and update operations run off the Editor UI thread, and
  failed additions clean safe partial-clone artifacts or report exact manual
  recovery steps.
- Dependency-install steps and their terminal result survive assembly reloads.
  An in-flight mutation is not issued twice; Unity's registered package state is
  rechecked, and the recovered success or failure is retained until it can be
  presented once.
- Credentials remain under the control of Git and GitHub CLI.
- Missing CLI tools are never installed by the package. Welcome links to official
  installation guidance, and authentication commands run only when the user
  copies them into a visible terminal.

See the [architecture and safety model](Documentation~/architecture.md) and
[security policy](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SECURITY.md)
for implementation and reporting details.

## Documentation

- [Installation and compatibility](Documentation~/installation.md)
- [User guide](Documentation~/user-guide.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Architecture and safety model](Documentation~/architecture.md)
- [Contributing](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/CONTRIBUTING.md)
- [Support](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SUPPORT.md)
- [Governance](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/GOVERNANCE.md)
- [Maintainers](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/MAINTAINERS.md)
- [Releasing](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/RELEASING.md)
- [Roadmap](Documentation~/roadmap.md)
- [Changelog](CHANGELOG.md)

## Contributing

Bug reports, documentation corrections, focused improvements, and
cross-platform test results are welcome. Read
[CONTRIBUTING.md](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/CONTRIBUTING.md)
before opening a pull request and follow the
[Code of Conduct](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/CODE_OF_CONDUCT.md).

## License

Distributed under the [MIT License](LICENSE.md).

Copyright (c) 2026 Martin Calander. The copyright and permission notice must be
retained with copies or substantial portions of the software. Additional
attribution information is available in [NOTICE.md](NOTICE.md) and
[AUTHORS.md](AUTHORS.md).

Git and the Git logo are either registered trademarks or trademarks of Software
Freedom Conservancy, Inc., corporate home of the Git Project. The Git logo was
created by Jason Long and is licensed under
[CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).

Git Submodule Manager is not sponsored by or affiliated with Unity Technologies
or its affiliates. Unity and the Unity logo are trademarks or registered
trademarks of Unity Technologies or its affiliates in the U.S. and elsewhere.
GitHub is a trademark of GitHub, Inc. This project is not endorsed by any of
those trademark owners.
