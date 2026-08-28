# Installation and Compatibility

## Prerequisites

### Unity

The validated Editor targets are exact Unity `6000.3.22f1` and Unity
`6000.5.*f1` final releases. Unity `6000.4` is not currently supported. The
package manifest declares `6000.3.22f1` as its minimum eligibility version. All
package assemblies are editor-only.

### Git CLI

Git is required. Confirm it is visible to your user account:

```bash
git --version
```

Official downloads are available at [git-scm.com](https://git-scm.com/downloads).
If Git is missing, the standalone Welcome window links to the official download
page. Install it outside Unity, then choose **Check Again**.

### GitHub CLI

GitHub CLI is optional but recommended. It enables authenticated GitHub
catalogue discovery, including inspection of repository manifests and Unity
meta files.

```bash
gh --version
gh auth login --hostname github.com --web
gh api user --hostname github.com --jq .login
```

Install it from [cli.github.com](https://cli.github.com/).
When GitHub CLI is missing, the Welcome window opens its official install guide.
When it is installed but unauthenticated, the window provides a copyable
`gh auth login` command and the authentication guide. Complete authentication
in a visible terminal, then choose **Check Again**. Git Submodule Manager never
accepts or stores a GitHub token.

## First Open

On a supported Unity version, open **Window > Package Management > Package
Manager** and select **GitHub** under **Sources**. Repositories appear
incrementally alongside installed GitHub packages. Select a package, review its
**Repository** link and branch, then choose **Install as Git Submodule** or
**Install as Read-Only Package** from **Install**. Choose **Refresh** to rescan.

If GitHub CLI is missing or authentication fails, installed GitHub packages
remain visible, and direct URL installation remains available from Package
Manager's **+ > Install package as Git Submodule...** command.

Direct URL installation may continue when the root `package.json` is valid but
`package.json.meta` is missing or invalid. The final confirmation then warns
that the repository could not be identified automatically as a Unity package.
If the meta file is valid, its GUID is tied to the inspected revision and
checked again after checkout.

The first time you open **Sources > GitHub**, a small standalone Welcome window
checks Git, GitHub CLI, and GitHub authentication for the current user. Unity
records that the window has been shown in the ignored, per-user `UserSettings/`
directory. Reopen it with **Show Welcome** under Unity's **Preferences > Git
Submodule Manager** page.

That Preferences page repeats the Welcome setup checks with installed versions,
authentication status, official install/help actions, and **Check Again**. It
also provides **Open GitHub Package Manager** and stores the following per-user
defaults and safety choices:

- initial repository visibility (**All Repositories** by default) and
  organization filters (blank for all owners by default);
- initial discovered-package install mode (**Git Submodule** by default);
- whether to install missing dependencies automatically when each one has a
  single unambiguous source;
- whether to skip the second confirmation for a clean, routine submodule
  removal or conversion.

Both options are off by default. Warnings about dirty, unpushed, changed, or
unverified work always appear.

## Add the Package with UPM

Open **Window > Package Management > Package Manager**, choose **+ > Install
package from git URL...**, and enter the tagged `v2.0.0` release:

```text
https://github.com/martincalander/GitSubmoduleManager.git#v2.0.0
```

Replace `v2.0.0` with another published release tag when upgrading. An
unqualified repository URL follows the mutable `main` branch and should be used
only for development or pre-release testing:

```text
https://github.com/martincalander/GitSubmoduleManager.git
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

Git Submodule Manager never initializes all project submodules automatically.
Use the explicit Git command above after cloning a team project.

## Compatibility

The supported Editor targets are listed under [Prerequisites](#unity). Unity
package manifests can express a minimum version, but not a disjoint support
set. For that reason, `package.json` uses `unity: 6000.3` with
`unityRelease: 22f1` to admit the supported 6000.3 target. Those fields do not
declare Unity 6000.4 support.

| Platform | CLI discovery locations |
| --- | --- |
| Windows | `PATH`, Git for Windows, GitHub CLI program directories |
| macOS | `PATH`, Homebrew on Apple Silicon and Intel, system paths |
| Linux | `PATH`, common system paths, `/snap/bin` |

Commands are started directly rather than through a shell. Local paths are
normalized for Git on each platform, including Windows repository locations.

Network packages must use HTTPS or SSH. Plaintext `http://` and `git://`
transports and URLs containing passwords or access tokens are rejected.
Explicit local paths and `file://` repositories remain available for local
development.

## Upgrade or Remove

Manage Git Submodule Manager itself from Unity's Package Manager. When an
installed package is a verified submodule, its native **Manage** menu provides
**Convert to Read-Only Package** and **Uninstall Submodule**. Eligible direct
read-only Git dependencies provide **Convert to Submodule**. Review
[CHANGELOG.md](../CHANGELOG.md) before changing versions.

### Migrating from Git Package Manager

Version 2.0 renamed the UPM package, assembly, namespace, and old public window
types. Pre-release revisions used `com.essentials.gitpackagemanager`, followed
by `com.martincalander.gitpackagemanager`. Existing Git URL installations must
replace whichever legacy dependency key appears in `Packages/manifest.json`;
changing only the revision is not sufficient:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleManager.git#<renamed-release-tag>"
  }
}
```

Remove the old `com.essentials.gitpackagemanager` or
`com.martincalander.gitpackagemanager` key and let Unity regenerate
`Packages/packages-lock.json`. Replace `<renamed-release-tag>` with a published
tag that includes the Git Submodule Manager identity. No 1.x tag was published
from this repository; `v2.0.0` is the first tag under the current identity.

For a submodule installation, also coordinate the parent repository's gitlink
and `.gitmodules` path from either
`Packages/com.essentials.gitpackagemanager` or
`Packages/com.martincalander.gitpackagemanager` to
`Packages/com.martincalander.gitsubmodulemanager`, then run
`git submodule sync --recursive`. Update downstream assembly definition
references from `MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`, and source namespaces from
`MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`.

The former management-window redirect and package menu were removed. Current
workflows use Unity's native Package Manager, so code that referenced the old
window type must be updated.

Serialized editor data carries Unity's migration metadata. Per-user preferences
are copied without deleting the original to
`UserSettings/GitSubmoduleManagerSettings.asset`, and interrupted-operation
state under the old Library and SessionState paths remains recoverable.

After this one-time identity migration, a dependency pinned to a Git tag can be
upgraded by changing its tag normally.
