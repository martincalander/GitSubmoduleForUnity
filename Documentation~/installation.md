# Installation and Compatibility

`main` contains the simplified status-and-sources version of Git Submodule
Manager. The former management workflow is preserved on
[`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).

## Install from Git

In **Window > Package Management > Package Manager**, choose **+ > Install
package from git URL...** and enter:

```text
https://github.com/martincalander/GitSubmoduleForUnity.git#main
```

The equivalent entry in `Packages/manifest.json` is:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleForUnity.git#main"
  }
}
```

Merge this entry into the existing manifest rather than replacing unrelated
dependencies. Git must be available to the Unity Editor process. `main` is a
moving branch; for a reproducible installation, replace it with a tested commit.
Unity records the resolved revision in `Packages/packages-lock.json`.

The simplified version has not been published as a new package release yet.
Existing `0.8.1` OpenUPM packages, release tarballs, and bootstrap downloads
contain the earlier workflow. The package version in the source manifest still
reads `0.8.1`; use the Git revision to identify the current source. A branch
promotion does not replace published artifacts or tags.

## Install from a Local Checkout

Clone the current source into a stable folder:

```bash
git clone --branch main https://github.com/martincalander/GitSubmoduleForUnity.git
```

In Package Manager, choose **+ > Install package from disk...** and select the
checkout's root `package.json`. Keep this checkout outside the project's
`Assets` and `Packages` directories when installing it as a local dependency.
Unity reads changes directly from that folder. Avoid committing a
machine-specific local dependency path into a shared project manifest.

## Install as a Submodule

From the root of a Git-tracked Unity project, run:

```bash
git submodule add -b main https://github.com/martincalander/GitSubmoduleForUnity.git Packages/com.martincalander.gitsubmodulemanager
```

This creates an editable embedded package. Review and commit the `.gitmodules`
entry and package gitlink in the parent project. The parent gitlink pins the
actual commit; the branch setting does not update it automatically. Use Git to
update the checkout or remove it later.

If the package is already installed, use Unity or Git to change that existing
installation first. Do not create a second copy with the same package ID.

## Prerequisites

### Unity

Local validation passed all 307 Editor tests on Unity `7000.0.0a10` on macOS.
See the [verification record](../.github/CONTRIBUTING.md#performance-and-stability-verification-on-2026-10-19)
for the exact checks and remaining untested targets.

The compatibility gate retains exact Unity `6000.3.22f1`, Unity `6000.5.*f1`
final releases, exact `6000.6.0f1` and `6000.6.2f1`, exact `6000.7.0b1` and
`6000.7.0b2` betas, and exact `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, and
`7000.0.0a10` alphas. Other Unity 7 builds, Unity `6000.4`, and other `6000.6`
releases remain unsupported. The remaining targets have not been retested with
the simplified implementation.

The manifest uses `unity: 6000.3` with `unityRelease: 22f1` because it can express
only a minimum eligibility version. All package assemblies are Editor-only.

### Git

Git is needed for local submodule detection, project submodule management, and
UPM Git dependencies. Confirm that Git is available with:

```bash
git --version
```

Official downloads are available at [git-scm.com](https://git-scm.com/downloads).
The status view does not use GitHub CLI or require GitHub account setup. Git or
Unity may still require repository access when you install private packages.

## First Open

Open **Window > Package Management > Package Manager**. Installed package
submodules receive **Submodule** labels. Select an installed package to use its
**Repository** link when available. It appears first beside Unity's Documentation,
Changelog, and Licenses links.
Choose **Sources > GitHub** to view installed GitHub submodules and UPM Git
dependencies. The source is available even when no package is selected.

There is no Welcome window or Preferences setup. The extension reads local
installed state; package changes remain with Unity and Git. See the
[user guide](user-guide.md) for details.

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
let Unity finish importing and refresh Package Manager.

## Update or Remove

Use Package Manager to update or remove an ordinary UPM installation of this
package. If installed as a submodule, use Git to change its revision or remove
it, then review and commit the parent gitlink or `.gitmodules` changes.

The extension blocks Unity's raw embedded-folder deletion for submodules and
unverified Git checkouts. It supplies no replacement removal workflow.
Review [CHANGELOG.md](../CHANGELOG.md) before changing versions.

## Earlier Workflow and Published Releases

Use [`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy)
if you need the previous remote discovery, install, conversion, or removal
workflows. It preserves the former `main`; the existing `v0.8.1` release is an
older tagged snapshot of that product direction. Their installation instructions
are in the [legacy installation guide](https://github.com/martincalander/GitSubmoduleForUnity/blob/legacy/Documentation~/installation.md).

| Source | Contents |
| --- | --- |
| Git URL ending in `#main` or a current checkout | Simplified status and sources |
| Git URL ending in `#legacy` | Former `main` with the fuller management workflow |
| `v0.8.1` Git tag, OpenUPM `0.8.1`, or signed release `.tgz` | Published earlier workflow |
| `GitSubmoduleManagerInstaller-0.8.1.unitypackage` | Bootstrap for the earlier OpenUPM release |

The bundled bootstrap rejects Unity 7. Use current `main` source for the
simplified Unity 7 integration. Signed release tarballs contain a release-only
attestation; Git and local-folder installs use source without that attestation.
No new release artifact is produced by promoting `main`.

### Moving from Legacy

Finish earlier installation, conversion, or removal operations before switching.
The simplified version does not resume those operations or own their recovery
files. Retain and inspect that evidence with Git before changing installations.
The existing installed packages remain in place.

### Migrating from Git Package Manager

Earlier development revisions used `com.essentials.gitpackagemanager`, then
`com.martincalander.gitpackagemanager`. The current package name is
`com.martincalander.gitsubmodulemanager`. For a UPM dependency, replace the earlier
manifest key and let Unity resolve the package. For a submodule, coordinate its
`.gitmodules` entry and parent gitlink with the package path.

Downstream assembly definitions should reference
`MartinCalander.GitSubmoduleManager.Editor`; the matching source namespace is
`MartinCalander.GitSubmoduleManager.Editor`. The former management window,
Preferences, and mutation recovery services are not present on `main`.
