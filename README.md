<p align="center">
  <img src="GitSubmoduleManagerIcon.png" alt="Git Submodule Manager" width="80" height="80">
</p>

# Git Submodule Manager

Manage Git-hosted Unity packages from Unity's native Package Manager.

<p align="center">
  <a href="https://github.com/martincalander/GitSubmoduleManager/actions/workflows/ci.yml"><img alt="Sanity Checks" src="https://github.com/martincalander/GitSubmoduleManager/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/martincalander/GitSubmoduleManager/releases"><img alt="Package version" src="https://img.shields.io/github/package-json/v/martincalander/GitSubmoduleManager?filename=package.json&label=package"></a>
  <img alt="Validated Unity targets: 6000.3.22f1 and 6000.5.*f1" src="https://img.shields.io/badge/Unity-6000.3.22f1%20%7C%206000.5.%2Af1-222C37?logo=unity&logoColor=white">
  <img alt="Editor only" src="https://img.shields.io/badge/scope-Editor%20only-555">
  <a href="LICENSE.md"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

Git Submodule Manager adds GitHub discovery and Git workflows directly to
Unity's Package Manager. Install root UPM repositories as editable submodules or
normal read-only Git dependencies, and convert eligible packages between the two
modes.

## Highlights

- Browse authenticated GitHub repositories under **Sources > GitHub**, with
  native search, sorting, refresh, loading state, and filters for downloaded
  packages, visibility, and organization.
- Install a discovered package from one **Install** menu as either a Git
  submodule or a read-only package. Branch selection prefers `main` when present.
- See organization groups and **Public** or **Private** repository badges in the
  native package list.
- Add a repository directly with **+ > Install package as Git Submodule...**.
- Convert eligible packages or uninstall submodules from Package Manager's
  native **Manage** menu.
- Validate root `package.json` files and Unity `package.json.meta` markers, then
  resolve missing dependencies before making changes.

## Requirements

| Dependency | Requirement |
| --- | --- |
| Unity | Exact `6000.3.22f1` and `6000.5.*f1` final releases; Unity `6000.4` is not supported |
| [Git](https://git-scm.com/downloads) | Required for package operations |
| [GitHub CLI](https://cli.github.com/) | Optional; required only for authenticated GitHub discovery |

The UPM manifest declares `6000.3.22f1` as its minimum eligibility version.
Unity manifests cannot encode the non-contiguous support matrix above, so that
minimum does not make Unity `6000.4` a supported target.

Git and GitHub CLI must be available to the Unity Editor process. The Welcome
window and **Preferences > Git Submodule Manager** show installation,
authentication, and version status. The package is Editor-only and supports
Windows, macOS, and Linux.

## Installation

In **Window > Package Management > Package Manager**, choose **+ > Install
package from git URL...** and enter:

```text
https://github.com/martincalander/GitSubmoduleManager.git#v2.0.0
```

An unqualified repository URL follows the mutable `main` branch and is intended
only for development or pre-release testing.

## Quick Start

1. Open Package Manager and select **GitHub** under **Sources**.
2. Filter or search the catalogue, then select a package and branch.
3. Open **Install** and choose **Install as Git Submodule** or **Install as
   Read-Only Package**.
4. Use **Manage** for supported conversions and submodule removal, or the **+**
   menu to install a submodule directly from a secure Git URL or local repository.

GitHub catalogue discovery uses `gh`; direct URL probing and package operations
use Git. Automatic catalogue eligibility requires both a valid UPM
`package.json` and a valid Unity `package.json.meta` at the repository root.
Because `package.json` is also used outside Unity, direct URL installation shows
a mandatory warning when the Unity marker is missing or invalid, but still lets
the user install an otherwise valid root UPM package they explicitly trust.
Read-only packages using a repository subdirectory cannot be converted to
submodules.

## Safety

- Managed submodules are restricted to validated direct children of `Packages/`.
- Remote repositories require secure HTTPS or SSH; explicit local repositories
  are also supported.
- Commands run without a shell, hidden credential prompts are disabled, and the
  package never installs CLI tools or stores credentials.
- Local, staged, untracked, unpushed, or unverified work is never silently
  discarded. Changes require confirmation when they can be assessed safely;
  ambiguous states are blocked.
- Mutations verify Git and package state, roll back only when cleanup ownership
  is proven, and otherwise provide recovery instructions.

See the [architecture and safety model](Documentation~/architecture.md) and
[security policy](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SECURITY.md)
for implementation details and recovery behavior.

## Documentation

- [Installation and compatibility](Documentation~/installation.md)
- [User guide](Documentation~/user-guide.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Architecture and safety model](Documentation~/architecture.md)
- [Changelog](CHANGELOG.md)
- [Contributing](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/CONTRIBUTING.md)
- [Support](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SUPPORT.md)

## License

Distributed under the [MIT License](LICENSE.md). See
[Third Party Notices](Third%20Party%20Notices.md), [NOTICE.md](NOTICE.md), and
[AUTHORS.md](AUTHORS.md) for attribution.
