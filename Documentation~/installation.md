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

## Installation Options

| Method | Best for | Uses the signed release artifact | Updates |
| --- | --- | :---: | --- |
| Unity bootstrap `.unitypackage` | Familiar Unity asset import | Yes, through OpenUPM | Through Package Manager |
| [OpenUPM](https://openupm.com/packages/com.martincalander.gitsubmodulemanager/) | Normal project use | Yes | Through Package Manager |
| GitHub Release `.tgz` | Manual or offline installation | Yes | Install a newer tarball manually |
| Git URL | Direct source installation | No | Change or update the Git reference |
| Local folder | Package development | No | Uses the files on disk |

The release workflow adds `package/.attestation.p7m` while producing the signed
`.tgz`. OpenUPM publishes that exact release archive. Git URL and local-folder
installs use repository source and do not contain the release-only attestation.
Unity verifies the signature on supported tarball packages, although users
outside the signing Unity organization might see a limited-trust status.

OpenUPM currently serves signed version `0.8.1`. If a future release is still
being imported, use its signed GitHub Release tarball for the same packaged
payload, or use its pinned Git URL when a source installation is acceptable.

Tarball and local-folder installs are recorded as local `file:` dependencies.
When sharing a project manifest, keep those files at a stable project-relative
path or avoid committing a machine-specific dependency path.

### Unity Bootstrap Installer

The GitHub Release includes
[`GitSubmoduleManagerInstaller-0.8.1.unitypackage`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/GitSubmoduleManagerInstaller-0.8.1.unitypackage)
for users who prefer Unity's custom-package import workflow:

1. Download the installer and the release
   [`SHA256SUMS`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/SHA256SUMS).
2. Optionally compare the installer SHA-256 with the matching checksum entry.
3. In Unity, choose **Assets > Import Package > Custom Package** and import all
   files from the installer.
4. Review the installer window. It shows the exact version, registry URL,
   package scope, and `Packages/manifest.json` path.
5. Choose **Install 0.8.1** to authorize the manifest change and network
   package resolution.

Import alone does not change the manifest or contact OpenUPM. After consent,
the bootstrap adds only the exact package scope and dependency. It then requires
Unity to report `0.8.1` as a direct registry package from
`https://package.openupm.com`, with no package errors and a loaded Editor
assembly. Only then does it move its own unchanged files to the operating
system Trash.

If installation or verification stops, the installer keeps its window,
manifest recovery evidence, and the exact original manifest bytes. Reopen it
with **Tools > Git Submodule Manager > Installer** to retry or restore. A later
manifest edit is never overwritten by automatic recovery.

OpenUPM can take a few minutes to import a newly published GitHub release. If
the installer is downloaded during that interval, it stops safely with its
recovery evidence intact. Use **Retry safely** after the matching version
appears on OpenUPM, or restore the original manifest.

The `.unitypackage` is a checksummed bootstrap, not the signed UPM payload. The
package it installs is the same signed `.tgz` served by OpenUPM.

### OpenUPM Registry (Recommended)

The [OpenUPM package page](https://openupm.com/packages/com.martincalander.gitsubmodulemanager/)
shows available versions and registry build status.

The OpenUPM CLI configures the scoped registry and package dependency from the
Unity project root:

```bash
npm install -g openupm-cli
openupm add com.martincalander.gitsubmodulemanager
```

To configure the registry in Unity instead:

1. Open **Edit > Project Settings > Package Manager**.
2. Add a scoped registry with these values:
   - **Name:** `OpenUPM`
   - **URL:** `https://package.openupm.com`
   - **Scope:** `com.martincalander.gitsubmodulemanager`
3. Apply the registry settings.
4. Open **Window > Package Management > Package Manager**.
5. Choose **+ > Install package by name...**.
6. Enter `com.martincalander.gitsubmodulemanager` and version `0.8.1`, then
   choose **Install**. Leave the version blank to use the latest compatible
   release instead.

The equivalent manifest entries are shown below. Merge them into the project's
existing `Packages/manifest.json` instead of replacing unrelated registries or
dependencies:

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.martincalander.gitsubmodulemanager"
      ]
    }
  ],
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "0.8.1"
  }
}
```

The exact package-name scope avoids routing unrelated
`com.martincalander` packages through OpenUPM.

### Signed GitHub Release Tarball

1. Download
   [`com.martincalander.gitsubmodulemanager-0.8.1.tgz`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/com.martincalander.gitsubmodulemanager-0.8.1.tgz)
   from the [`v0.8.1` release](https://github.com/martincalander/GitSubmoduleForUnity/releases/tag/v0.8.1).
2. Optionally download [`SHA256SUMS`](https://github.com/martincalander/GitSubmoduleForUnity/releases/download/v0.8.1/SHA256SUMS)
   and compare its SHA-256 value with the tarball before installation.
3. Open **Window > Package Management > Package Manager**.
4. Choose **+ > Install package from tarball** and select the `.tgz` file.

Unity lists a tarball installation as a local package. To upgrade it, download
and install the signed tarball from the newer GitHub Release.

### Git URL

Open **Window > Package Management > Package Manager**, choose **+ > Install
package from git URL...**, and enter:

```text
https://github.com/martincalander/GitSubmoduleForUnity.git#v0.8.1
```

This URL pins the published `0.8.1` release. Omitting `#v0.8.1` follows the
mutable `main` branch and is intended only for development. Git must be visible
to the Unity Editor process. This method installs the tagged repository source
instead of the signed release tarball.

The equivalent dependency entry is:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleForUnity.git#v0.8.1"
  }
}
```

### Local Folder for Development

Clone or check out the repository to a stable location outside the Unity
project's `Assets` and `Packages` folders. In Package Manager, choose **+ >
Install package from disk...** and select the repository's root `package.json`.

Unity reads changes directly from that local folder. This method is intended
for package development and does not use the signed release artifact. A clone
placed directly at
`Packages/com.martincalander.gitsubmodulemanager` is an embedded package and is
also mutable.

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

During pre-release development, the UPM package, assembly, namespace, and old
public window types were renamed. Earlier revisions used
`com.essentials.gitpackagemanager`, followed by
`com.martincalander.gitpackagemanager`. Existing Git URL installations must
replace whichever legacy dependency key appears in `Packages/manifest.json`;
changing only the revision is not sufficient:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleForUnity.git#v0.8.1"
  }
}
```

Remove the old `com.essentials.gitpackagemanager` or
`com.martincalander.gitpackagemanager` key and let Unity regenerate
`Packages/packages-lock.json`. No earlier version tag was published from this
repository.

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
