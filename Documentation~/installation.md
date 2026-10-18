# Installation and Compatibility

## Test the Simplified Branch

This guide covers the experimental `simplify-package-status` branch and the
existing release distribution methods. Published `0.8.1` artifacts contain the
earlier management workflow, so use a local checkout to test this branch.

In a test project, choose **+ > Install package from disk...** in Package Manager
and select the checkout's root `package.json`. An existing checkout at
`Packages/com.martincalander.gitsubmodulemanager` also works as an embedded
package. Finish any operation in the earlier implementation before switching
branches, and retain any existing recovery files until inspected.

Local validation of the simplified branch passed on Unity `7000.0.0a10` on
macOS. See the [verification record](../.github/CONTRIBUTING.md#performance-verification-on-2026-10-19)
for the final checks. This experiment is not a new published release.

## Prerequisites

### Unity

The existing compatibility gate retains exact Unity `6000.3.22f1`, Unity
`6000.5.*f1` final releases, exact `6000.6.0f1` and `6000.6.2f1`, exact
`6000.7.0b1` and `6000.7.0b2` betas, and exact `7000.0.0a7`, `7000.0.0a8`,
`7000.0.0a9`, and `7000.0.0a10` alphas. Other Unity 7 builds, Unity `6000.4`,
and other `6000.6` releases remain unsupported. Retaining this gate does not
claim that every target has been retested with the simplified implementation.

The manifest uses `unity: 6000.3` with `unityRelease: 22f1` because it can express
only a minimum eligibility version. All package assemblies are Editor-only.

### Git

Use Git to install and manage project submodules. Unity also requires Git for
UPM Git dependencies. Confirm that your Git installation is available with:

```bash
git --version
```

Official downloads are available at [git-scm.com](https://git-scm.com/downloads).
The simplified status view has no GitHub CLI or authentication setup requirement
and does not contact GitHub.

## Published Release Installation Options

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

These examples pin the signed `0.8.1` release. If a future release is still
being imported, use its signed GitHub Release tarball for the same packaged
payload, or use its pinned Git URL when a source installation is acceptable.

Tarball and local-folder installs are recorded as local `file:` dependencies.
When sharing a project manifest, keep those files at a stable project-relative
path or avoid committing a machine-specific dependency path.

### Unity Bootstrap Installer

The bundled `.unitypackage` bootstrap retains its validated Unity 6 targets:
exact `6000.3.22f1`, `6000.5.*f1` final releases, exact `6000.6.0f1` and
`6000.6.2f1`, and exact `6000.7.0b1` and `6000.7.0b2` betas. It rejects Unity 7.
For the `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, and `7000.0.0a10` alphas, use a direct UPM Git URL pinned to a
revision containing the Unity 7 compatibility changes, or a local checkout of that
revision. The released `v0.8.1` installation examples below describe the
Unity 6 workflow.

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

Open **Window > Package Management > Package Manager** and select an installed
package to inspect its source and submodule status. Select **Sources > GitHub**
to view installed GitHub submodules and UPM Git dependencies. The page reads
installed project state and does not scan remote repositories.

There is no Welcome window or Preferences setup. Package changes belong to
Unity and Git. See the [user guide](user-guide.md) for the reduced workflow.

## Team Clone Setup

Clone a project and initialize its registered submodules with:

```bash
git clone --recurse-submodules <project-url>
```

For an existing clone:

```bash
git submodule update --init --recursive
```

The extension does not initialize submodules automatically. Each teammate and
CI identity needs Git access to the relevant repositories. After initialization,
let Unity finish import and refresh Package Manager.

## Upgrade or Remove

Use Package Manager to update or remove an ordinary UPM installation of this
package. If it is installed as a submodule, use Git to change its revision or
remove it. The extension blocks Unity's raw embedded-folder deletion for
submodules and supplies no replacement mutation workflow.

Review [CHANGELOG.md](../CHANGELOG.md) before changing versions. The simplified
branch does not resume earlier installation, conversion, or removal operations.
Inspect retained recovery evidence before continuing an interrupted operation.

### Migrating from Git Package Manager

Earlier development revisions used `com.essentials.gitpackagemanager`, then
`com.martincalander.gitpackagemanager`. The current package name is
`com.martincalander.gitsubmodulemanager`. For a UPM dependency, replace the legacy
manifest key and let Unity resolve the updated package. For a submodule,
coordinate its `.gitmodules` entry and parent gitlink with the package path.

Downstream assembly definitions should reference
`MartinCalander.GitSubmoduleManager.Editor`; the matching source namespace is
`MartinCalander.GitSubmoduleManager.Editor`. The old management window and its
redirect are unavailable. This branch also removes the earlier per-user
Preferences and mutation recovery services, so inspect and preserve any old
recovery files manually before changing installations.
