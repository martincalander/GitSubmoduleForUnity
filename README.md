<p align="center">
  <a href="https://git-scm.com/" title="Git">
    <picture>
      <img src="GPMIcon.png" alt="Git" width="72" height="72">
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

# Git Package Manager

Manage Unity packages as real Git submodules, directly from the Editor.

<p align="center">
  <a href="https://github.com/martincalander/GitPackageManager/actions/workflows/ci.yml"><img alt="Sanity Checks" src="https://github.com/martincalander/GitPackageManager/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/martincalander/GitPackageManager/releases"><img alt="Package version" src="https://img.shields.io/github/package-json/v/martincalander/GitPackageManager?filename=package.json&label=package"></a>
  <img alt="Unity 2021.3 or newer" src="https://img.shields.io/badge/Unity-2021.3%2B-222C37?logo=unity&logoColor=white">
  <img alt="Editor only" src="https://img.shields.io/badge/scope-Editor%20only-555">
  <a href="LICENSE.md"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

Git Package Manager is an editor-only Unity package for installing and managing
UPM packages as Git submodules under `Packages/`. It provides a focused UI for
the Git operations while keeping the resulting repository structure completely
standard and visible to normal Git tooling.

## What It Does

- Lists Git submodules installed directly under `Packages/`.
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
tools, or manage packages outside `Packages/`.

## Requirements

| Dependency | Requirement | Used for |
| --- | --- | --- |
| Unity | 2021.3 or newer | Editor host and UPM support |
| [Git CLI](https://git-scm.com/downloads) | Required | All submodule operations |
| [GitHub CLI](https://cli.github.com/) | Optional, recommended | Authenticated repository discovery |

Git must be available to the Unity Editor process. GitHub CLI is needed only
for GitHub repository discovery. If a tool is missing, Git Package Manager
shows an official installation link and a platform-specific command. On
supported macOS and Windows setups, it can run that native command only after
showing it and receiving your explicit confirmation. Linux installation stays
in your terminal so administrator prompts remain visible. The first time the
window is opened in a project, a guided setup page checks both tools and offers
the same install actions. If `gh` is installed but not authenticated, the page
can start GitHub CLI's device login, open GitHub's device page, and verify the
resulting session. One-click login requires GitHub CLI 2.79.0 or newer; older
versions keep a compatible visible-terminal command available.

## Installation

Open **Window > Package Manager**, select **Add package from git URL…**, and
enter:

```text
https://github.com/martincalander/GitPackageManager.git
```

Git Package Manager itself is installed as a UPM Git dependency. Packages
managed by the tool are still added to the project as Git submodules.

## Usage

1. Open **Window > Package Management > Git Package Manager**.
2. Complete the one-time setup page. Git is required; GitHub CLI is optional
   but recommended for repository discovery.
3. If prompted, install Git or GitHub CLI with explicit approval, then choose
   **Authenticate with GitHub...** to complete the browser login.
4. Use **In Project** to manage installed package submodules.
5. Use **GitHub** to find package repositories visible to your account.
6. Use the **+** menu to add a repository directly by URL.

Every mutating operation requires an explicit action in the window. Progress,
command failures, validation results, and recovery instructions are reported in
the Editor.

## Repository Discovery

GitHub discovery is designed for accounts and organizations with hundreds of
repositories:

- results are requested 50 repositories at a time;
- searches execute through the GitHub API rather than filtering a full local
  download;
- owner, search, and page changes cancel stale in-flight results;
- package validation normally runs only for the selected repository;
- the **Valid UPM Packages** filter checks the current page in small batches
  and shows only repositories with a valid root manifest;
- branch information is loaded only when requested.

Without GitHub CLI, direct Git URLs and installed-submodule management remain
available. Git is the only dependency used by the **+ > Add Submodule...**
workflow; `gh` is required only for the **GitHub** discovery tab.

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
- A newly cloned repository is rolled back when its root package validation
  fails. The manifest must be a regular, bounded UTF-8 file with a reverse-domain
  package name and a SemVer 2.0 version.
- Persisted `.gitmodules`, local Git configuration, and worktree origins are
  revalidated before a mutation, and incomplete structural Git output fails
  closed instead of being partially trusted.
- Network-heavy add and update operations run off the Editor UI thread, and
  failed additions clean safe partial-clone artifacts or report exact manual
  recovery steps.
- Credentials remain under the control of Git and GitHub CLI.
- Missing CLI tools are never installed silently; the exact native command is
  shown in a confirmation dialog before it can run.

See the [architecture and safety model](Documentation~/architecture.md) and
[security policy](https://github.com/martincalander/GitPackageManager/blob/main/.github/SECURITY.md)
for implementation and reporting details.

## Documentation

- [Installation and compatibility](Documentation~/installation.md)
- [User guide](Documentation~/user-guide.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Architecture and safety model](Documentation~/architecture.md)
- [Contributing](https://github.com/martincalander/GitPackageManager/blob/main/.github/CONTRIBUTING.md)
- [Support](https://github.com/martincalander/GitPackageManager/blob/main/.github/SUPPORT.md)
- [Governance](https://github.com/martincalander/GitPackageManager/blob/main/.github/GOVERNANCE.md)
- [Maintainers](https://github.com/martincalander/GitPackageManager/blob/main/.github/MAINTAINERS.md)
- [Releasing](https://github.com/martincalander/GitPackageManager/blob/main/.github/RELEASING.md)
- [Roadmap](Documentation~/roadmap.md)
- [Changelog](CHANGELOG.md)

## Contributing

Bug reports, documentation corrections, focused improvements, and
cross-platform test results are welcome. Read
[CONTRIBUTING.md](https://github.com/martincalander/GitPackageManager/blob/main/.github/CONTRIBUTING.md)
before opening a pull request and follow the
[Code of Conduct](https://github.com/martincalander/GitPackageManager/blob/main/.github/CODE_OF_CONDUCT.md).

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

Git Package Manager is not sponsored by or affiliated with Unity Technologies
or its affiliates. Unity and the Unity logo are trademarks or registered
trademarks of Unity Technologies or its affiliates in the U.S. and elsewhere.
GitHub is a trademark of GitHub, Inc. This project is not endorsed by any of
those trademark owners.
