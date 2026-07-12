# Installation and Compatibility

## Prerequisites

### Unity

Unity 2021.3 or newer is required. All package assemblies are editor-only.

### Git CLI

Git is required. Confirm it is visible to your user account:

```bash
git --version
```

Official downloads are available at [git-scm.com](https://git-scm.com/downloads).
If Git is missing, the editor window offers platform-appropriate installation
help. On supported macOS and Windows setups it can run the displayed native
installer command only after you explicitly approve it.

### GitHub CLI

GitHub CLI is optional but recommended. It enables authenticated user and
organization discovery plus remote `package.json` validation.

```bash
gh --version
gh auth login
gh auth status -h github.com
```

Install it from [cli.github.com](https://cli.github.com/).
The editor provides the same opt-in assistance when GitHub CLI is missing.
Linux commands remain in your terminal so administrator prompts are visible.

## Add the Package with UPM

Use **Window > Package Manager > + > Add package from git URL…**:

```text
https://github.com/martincalander/GitSubmoduleForUnity.git
```

To install a specific released version, append a Git tag:

```text
https://github.com/martincalander/GitSubmoduleForUnity.git#v1.0.0
```

## Team Clone Setup

Clone a project and initialize all registered submodules with:

```bash
git clone --recurse-submodules <project-url>
```

For an existing clone:

```bash
git submodule update --init --recursive
```

The editor window never initializes all submodules automatically. Uninitialized
packages are shown explicitly and can be initialized one at a time.

## Compatibility

| Platform | CLI discovery locations |
| --- | --- |
| Windows | `PATH`, Git for Windows, GitHub CLI program directories |
| macOS | `PATH`, Homebrew on Apple Silicon and Intel, system paths |
| Linux | `PATH`, common system paths, `/snap/bin` |

The package uses `System.Diagnostics.Process` without a shell, so command
arguments follow the platform's normal process rules. Paths are normalized for
Git configuration while Windows backslashes are preserved when quoting local
repository locations.

## Upgrade or Remove

Manage Git Package Manager itself from Unity's Package Manager. Review
[CHANGELOG.md](../CHANGELOG.md) before changing versions. If the dependency is
pinned to a Git tag in `Packages/manifest.json`, change that tag to upgrade.
