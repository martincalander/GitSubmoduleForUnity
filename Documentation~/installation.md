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

### GitHub CLI

GitHub CLI is optional but recommended. It enables authenticated user and
organization discovery plus remote `package.json` validation.

```bash
gh --version
gh auth login
gh auth status -h github.com
```

Install it from [cli.github.com](https://cli.github.com/).

## Add the Package

### As a submodule

From the Unity project root:

```bash
git submodule add \
  https://github.com/martincalander/GitSubmoduleForUnity.git \
  Packages/com.essentials.gitpackagemanager
```

Then commit `.gitmodules` and the submodule entry.

### From a UPM Git URL

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

## Upgrade

When the package itself is installed as a submodule:

```bash
git -C Packages/com.essentials.gitpackagemanager fetch --tags
git -C Packages/com.essentials.gitpackagemanager checkout <tag-or-commit>
git add Packages/com.essentials.gitpackagemanager
```

Review [CHANGELOG.md](../CHANGELOG.md) before changing versions.

## Remove

To remove the tool itself from a project:

```bash
git submodule deinit -f -- Packages/com.essentials.gitpackagemanager
git rm -f -- Packages/com.essentials.gitpackagemanager
```

Commit the resulting `.gitmodules` and Git index changes.
