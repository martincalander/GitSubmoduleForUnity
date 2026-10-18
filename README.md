<p align="center">
  <img src="Documentation~/Images/GitSubmoduleManagerCover.png" alt="Git Submodule Manager for Unity" width="600">
</p>

# Git Submodule Manager

See how your installed Unity packages are sourced in the native Package Manager.

<p align="center">
  <a href="https://github.com/martincalander/GitSubmoduleForUnity/actions/workflows/ci.yml"><img alt="Sanity Checks" src="https://github.com/martincalander/GitSubmoduleForUnity/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/martincalander/GitSubmoduleForUnity/releases"><img alt="Package version" src="https://img.shields.io/github/package-json/v/martincalander/GitSubmoduleForUnity?filename=package.json&label=package"></a>
  <img alt="Editor only" src="https://img.shields.io/badge/scope-Editor%20only-555">
  <a href="LICENSE.md"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-blue"></a>
</p>

This experimental `simplify-package-status` branch reduces the package to status
and sources. The simplified implementation passed local validation on Unity
`7000.0.0a10` on macOS. See the [verification record](.github/CONTRIBUTING.md#performance-verification-on-2026-10-19)
for the final checks. This branch is not a new published release.

## What It Shows

- Installed package submodules are labelled **Submodule** in Package Manager.
- Selected packages show their installation source and submodule status.
- **Sources > GitHub** groups the project's already-installed GitHub submodules
  and UPM Git dependencies in Unity's native package list.

The GitHub page reads installed project state. It does not scan your account or
contact GitHub, and it needs no GitHub CLI or authentication setup.

Installation, updates, conversion, and removal belong to Unity and Git. The
extension prevents Unity's raw embedded-package removal from deleting a
submodule folder. Manage submodules with Git, then let Unity refresh its package
state. Ordinary packages keep Unity's standard actions.

## Try This Branch

Use the local checkout of `simplify-package-status` in a test project. In Package
Manager, choose **+ > Install package from disk...** and select its root
`package.json`, or use an existing checkout at
`Packages/com.martincalander.gitsubmodulemanager`.

1. Open **Window > Package Management > Package Manager**.
2. Select an installed package and inspect its status.
3. Choose **Sources > GitHub** to see installed packages hosted on GitHub.
4. Change package or submodule state using Unity or Git, then refresh Package
   Manager to see the updated status.

Published `0.8.1` artifacts contain the earlier management workflow. They do not
contain this experiment. The bundled `.unitypackage` bootstrap rejects Unity 7;
use this checkout to test the simplified Unity 7 integration.

## Compatibility

The existing compatibility gate retains exact Unity `6000.3.22f1`,
`6000.5.*f1` final releases, exact `6000.6.0f1` and `6000.6.2f1`, exact
`6000.7.0b1` and `6000.7.0b2` betas, and exact `7000.0.0a7`, `7000.0.0a8`,
`7000.0.0a9`, and `7000.0.0a10` alphas. Other Unity 7 versions, Unity `6000.4`,
and other `6000.6` versions remain unsupported. Retaining this gate does not
claim that the simplified implementation has been retested on every target.

The UPM manifest declares `6000.3.22f1` as its minimum eligibility version. Unity
manifests cannot express the complete support matrix. The package is Editor-only.

## Installation and Documentation

The [installation guide](Documentation~/installation.md) covers local testing,
team submodule setup, and the existing OpenUPM, signed tarball, Git URL, and
bootstrap distribution methods.

- [User guide](Documentation~/user-guide.md)
- [Troubleshooting](Documentation~/troubleshooting.md)
- [Architecture and safety model](Documentation~/architecture.md)
- [Roadmap](Documentation~/roadmap.md)
- [Changelog](CHANGELOG.md)
- [Contributing](.github/CONTRIBUTING.md)
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
