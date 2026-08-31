<p align="center">
  <img src="Documentation~/Images/GitSubmoduleManagerCover.png" alt="Git Submodule Manager for Unity" width="600">
</p>

# Git Submodule Manager

Manage Git-hosted Unity packages from Unity's native Package Manager.

<p align="center">
  <a href="https://github.com/martincalander/GitSubmoduleForUnity/actions/workflows/ci.yml"><img alt="Sanity Checks" src="https://github.com/martincalander/GitSubmoduleForUnity/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/martincalander/GitSubmoduleForUnity/releases"><img alt="Package version" src="https://img.shields.io/github/package-json/v/martincalander/GitSubmoduleForUnity?filename=package.json&label=package"></a>
  <img alt="Validated Unity targets: 6000.3.22f1 and 6000.5.*f1" src="https://img.shields.io/badge/Unity-6000.3.22f1%20%7C%206000.5.%2Af1-222C37?logo=unity&logoColor=white">
  <img alt="Editor only" src="https://img.shields.io/badge/scope-Editor%20only-555">
  <a href="LICENSE.md"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

If NuGetForUnity makes NuGet packages feel at home in Unity, Git Submodule
Manager does the same for Git submodules. It brings Git-hosted UPM packages into
Unity's own Package Manager, where you can browse GitHub repositories, install a
package as an editable submodule or read-only Git dependency, and switch
eligible packages between the two modes without leaving the Editor.

## Highlights

- Browse authenticated GitHub repositories under **Sources > GitHub**, with
  native search, sorting, refresh, loading state, and filters for downloaded
  packages, visibility, and organization.
- Install a discovered package from one **Install** menu as either a Git
  submodule or a read-only package. Branch selection prefers `main` when present.
- See organization groups and **Public** or **Private** repository badges in the
  native package list.
- Add a repository directly with **+ > Install package as Git Submodule...**.
- Remove one or more submodules with Package Manager's standard action, including
  mixed selections with ordinary packages, and convert eligible packages from
  its native **Manage** menu. Ordinary-only selections remain Unity-native.
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

| Method | Best for | Uses the signed release artifact |
| --- | --- | :---: |
| [Unity bootstrap `.unitypackage`](#unity-bootstrap-installer) | Familiar Unity asset import | Yes, through OpenUPM |
| [OpenUPM](https://openupm.com/packages/com.martincalander.gitsubmodulemanager/) | Normal installation and updates | Yes |
| [GitHub Release `.tgz`](#signed-github-release-tarball) | Manual or offline installation | Yes |
| [Git URL](#git-url) | Installing source directly from a tag | No |
| [Local folder](Documentation~/installation.md#local-folder-for-development) | Package development | No |

### Unity Bootstrap Installer

Download
[`GitSubmoduleManagerInstaller-0.8.1.unitypackage`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/GitSubmoduleManagerInstaller-0.8.1.unitypackage)
from the [`v0.8.1` release](https://github.com/martincalander/GitSubmoduleForUnity/releases/tag/v0.8.1),
then import it with **Assets > Import Package > Custom Package**.

Importing the bootstrap does not change the project or contact the network.
Review its exact plan, then choose **Install 0.8.1**. It configures only the
exact OpenUPM package scope, installs the signed registry package, verifies its
version and source, and moves its own unchanged bootstrap files to Trash.

### OpenUPM (Recommended)

View releases and package details on the
[Git Submodule Manager OpenUPM page](https://openupm.com/packages/com.martincalander.gitsubmodulemanager/).

From the Unity project root, use the OpenUPM CLI:

```bash
openupm add com.martincalander.gitsubmodulemanager
```

Without the CLI, add `https://package.openupm.com` as a scoped registry for
`com.martincalander.gitsubmodulemanager`. Then open **Window > Package
Management > Package Manager**, choose **+ > Install package by name...**, and
enter the package name and version `0.8.1`.

### Signed GitHub Release Tarball

Download the
[signed `0.8.1` `.tgz`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/com.martincalander.gitsubmodulemanager-0.8.1.tgz),
open Unity's Package Manager, and choose **+ > Install package from tarball**.
Select the downloaded file. Install newer releases manually in the same way.

### Git URL

In Unity's Package Manager, choose **+ > Install package from git URL...** and
enter:

```text
https://github.com/martincalander/GitSubmoduleForUnity.git#v0.8.1
```

This URL pins the published `0.8.1` release. Omitting `#v0.8.1` follows the
mutable `main` branch and is intended only for development. A Git URL installs
the repository source, not the signed release tarball.

See the [installation and compatibility guide](Documentation~/installation.md)
for scoped-registry settings, manifest JSON, checksum verification, and local
development setup.

## Quick Start

1. Open Package Manager and select **GitHub** under **Sources**.
2. Filter or search the catalogue, then select a package and branch.
3. Open **Install** and choose **Install as Git Submodule** or **Install as
   Read-Only Package**.
4. Use Unity's standard **Remove** action for one or more selected submodules,
   **Manage** for supported conversions, or the **+** menu to install a
   submodule directly from a secure Git URL or local repository.

The manager overrides **Remove** only when the action includes a managed
submodule. It waits for package scans, presents confirmation when required,
removes the submodules and every ordinary package in the action, and asks Unity
to resolve as needed. Do not click **Remove** again or manually refresh or resolve.
Ordinary-only selections, including read-only Git packages, keep Unity Package
Manager's normal removal behavior.

GitHub catalogue discovery uses `gh`; direct URL probing and package operations
use Git. Catalogue entries need a valid root UPM `package.json` and Unity
`package.json.meta`, which helps keep ordinary npm repositories out of automatic
discovery. Direct URL installs may proceed without a valid meta file, but only
after a mandatory warning. Read-only dependencies that point to a repository
subdirectory cannot be converted to submodules.

## Safety

- Managed submodules are restricted to validated direct children of `Packages/`.
- Remote repositories require secure HTTPS or SSH; explicit local repositories
  are also supported.
- Commands run without a shell, hidden credential prompts are disabled, and the
  package never installs CLI tools or stores credentials.
- The manager never silently deletes local, staged, untracked, unpushed, or
  unverified work. Changes require confirmation when their state can be checked;
  ambiguous states are blocked.
- Rollback removes only state that can be tied to the current operation. Anything
  uncertain is left in place with recovery instructions.

See the [architecture and safety model](Documentation~/architecture.md) and
[security policy](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SECURITY.md)
for implementation details and recovery behavior.

## Documentation

- [Installation and compatibility](Documentation~/installation.md)
- [User guide](Documentation~/user-guide.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Architecture and safety model](Documentation~/architecture.md)
- [Changelog](CHANGELOG.md)
- [Contributing](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/CONTRIBUTING.md)
- [Support](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SUPPORT.md)

## License

Distributed under the [MIT License](LICENSE.md). See
[Third Party Notices](Third%20Party%20Notices.md), [NOTICE.md](NOTICE.md), and
[AUTHORS.md](AUTHORS.md) for attribution.

## Games Made Using This Package

| [![Cave Expedition Steam capsule](https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/4372950/capsule_616x353.jpg)](https://store.steampowered.com/app/4372950/Cave_Expedition/) | [![Big Boy Boxing Steam capsule](https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1680780/capsule_616x353.jpg)](https://store.steampowered.com/app/1680780/Big_Boy_Boxing/) |
| :---: | :---: |
| [**Cave Expedition**](https://store.steampowered.com/app/4372950/Cave_Expedition/) | [**Big Boy Boxing**](https://store.steampowered.com/app/1680780/Big_Boy_Boxing/) |

Made a game using this package? Email
[martin.calander@gmail.com](mailto:martin.calander@gmail.com) to have it added
to the showcase.
