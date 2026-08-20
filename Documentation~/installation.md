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
gh auth login --hostname github.com --web
gh api user --hostname github.com --jq .login
```

Install it from [cli.github.com](https://cli.github.com/).
The editor provides the same opt-in assistance when GitHub CLI is missing.
Linux install commands remain in your terminal so administrator prompts are
visible. When GitHub CLI is installed but unauthenticated, the welcome page and
GitHub dependency card can start its device login. Unity opens GitHub's device
page, GitHub CLI copies the one-time code to the clipboard, and the editor makes
a fresh authenticated `gh api user` request before enabling discovery. If
clipboard access fails, cancel the in-editor flow and run the displayed command
in a visible terminal after cancellation finishes so the code remains visible.
If Unity cannot confirm the authentication process stopped, restart Unity before
retrying. One-click login requires GitHub CLI
2.79.0 or newer. The approval dialog also discloses that its non-interactive
flow selects HTTPS as GitHub CLI's host-wide Git protocol; the terminal fallback
leaves that choice interactive.

## First Open

Open **Window > Package Management > Git Submodule Manager**. A one-time welcome
page checks Git, GitHub CLI, and GitHub authentication for the current user. Its
shown flag is stored per user and project under Unity's ignored
`UserSettings/` directory. The page can be opened again manually from
**Welcome & Setup...** in the window menu.

The same per-user file stores the Git Submodule Manager options shown under
Unity's **Preferences > Git Submodule Manager** page. That page also provides an
**Open Welcome & Setup** button.

## Add the Package with UPM

Use **Window > Package Manager > + > Add package from git URL…**:

```text
https://github.com/martincalander/GitSubmoduleManager.git
```

To install a specific released version, append a Git tag:

```text
https://github.com/martincalander/GitSubmoduleManager.git#<version-tag>
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

Network packages must use HTTPS or SSH. Plaintext `http://` and `git://`
transports and URLs containing passwords or access tokens are rejected.
Explicit local paths and `file://` repositories remain available for local
development.

## Upgrade or Remove

Manage Git Submodule Manager itself from Unity's Package Manager. Review
[CHANGELOG.md](../CHANGELOG.md) before changing versions.

### Migrating from Git Package Manager

The rename changes the UPM package, assembly, namespace, and public window type
identities. Existing Git URL installations must replace the dependency key in
`Packages/manifest.json`; changing only the tag is not sufficient:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleManager.git#<renamed-release-tag>"
  }
}
```

Remove the old `com.martincalander.gitpackagemanager` key and let Unity
regenerate `Packages/packages-lock.json`. Replace `<renamed-release-tag>` with a
published tag that includes the Git Submodule Manager identity; the older
`v1.0.0` release predates this rename.

For a submodule installation, also coordinate the parent repository's gitlink
and `.gitmodules` path from
`Packages/com.martincalander.gitpackagemanager` to
`Packages/com.martincalander.gitsubmodulemanager`, then run
`git submodule sync --recursive`. Update downstream assembly definition
references from `MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`, source namespaces from
`MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`, and window references from
`GitPackageManagerWindow` to `GitSubmoduleManagerWindow`.

Serialized editor types carry Unity migration metadata. Per-user preferences
are copied non-destructively to `UserSettings/GitSubmoduleManagerSettings.asset`,
and interrupted-operation state under the legacy Library and SessionState paths
remains recoverable.

After this one-time identity migration, a dependency pinned to a Git tag can be
upgraded by changing its tag normally.
