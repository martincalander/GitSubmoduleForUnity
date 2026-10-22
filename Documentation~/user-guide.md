# User Guide

Open **Window > Package Management > Package Manager**. Git Submodule Manager
adds submodule labels, a native **Repository** link, and **Sources > GitHub**.
This is the current design on `main`. See
[compatibility](installation.md#unity) for the exact Editor targets and local
verification limits.

## Package Status

An installed package recognised as a submodule is labelled **Submodule** in the
package list. Other packages retain their Unity installation
source, such as a registry, Git dependency, or local package.

Submodule classification works across Git hosts, including private hosts and
relative submodule URLs. A package must be installed and resolved by Unity and
have a verified submodule registration to receive the label. Ordinary UPM Git
dependencies retain their Git source even when hosted on the same server.

Missing or inconsistent Git registration remains unverified. The extension
does not repair registration or change files. See
[troubleshooting](troubleshooting.md#a-package-is-not-labelled-submodule) to
check the project's Git metadata and package paths.

Submodule status describes how the package is installed. It does not report
whether the checkout is clean, up to date, or pushed. Use your Git client for
those details.

## GitHub Source

Choose **Sources > GitHub** to see installed packages hosted on GitHub:

- package submodules registered by the project;
- UPM Git dependencies resolved by Unity.

The page uses Unity's native package list, search, sorting, and selection. It
contains installed packages only, so an empty page is expected in a project
without GitHub packages. Repositories that you own or can access do not appear
unless a package from them is already installed.

When Unity changes an installed package's source, GitHub membership waits for
matching installed metadata instead of retaining the previous source's listing.

Opening this page does not query GitHub, read account credentials, or require
GitHub CLI. Private installed packages appear from their local source metadata.
Their visibility does not grant access to teammates or build machines.

## Repository Link

Select an installed package. When a safe repository URL is available,
**Repository** appears first in Unity's native links row, before Documentation,
Changelog, and Licenses. It opens the repository in your browser. Unity's other
links and package information remain available as usual.

The URL comes from the installed package's cached source metadata. HTTP and
HTTPS sources can be opened directly; supported public Git hosts also have
browser links for SSH sources. Relative, local, and unknown SSH sources have no
guessed browser link.

Repository links omit embedded credentials, unrelated query parameters, the
package subdirectory, and the revision suffix. Opening a private repository may
require signing in through your browser. The link does not edit the package or
change its installation source.

## Changing Packages

### Add from a Git Link

Choose **+ > Install package as Git submodule...**, paste a repository URL, and
choose **Install**. The repository must contain a valid `package.json` at its
root. Its package name determines the destination, `Packages/<package-name>`.
The project must be the root of a Git repository.

Use HTTPS or SSH, with an optional `#branch` suffix. Without a suffix, the
repository's default branch is used. Installation checks the root package
manifest and destination, then adds the submodule. It does not browse an
account, discover other repositories, or plan dependency installations.
Repositories with packages only in a subfolder are not supported by this action.
An existing installation must be removed before adding the same package again.

Unity resolves and imports the package after installation. Review and commit
the `.gitmodules` and gitlink changes in the parent repository. Gitlinks record
the installed commit; use Git to update the submodule later.

### Remove a Submodule Package

Select an installed submodule and use **Remove**, then confirm the removal.
The extension removes the package folder, parent gitlink, submodule
registration, and any same-name dependency in `Packages/manifest.json` so that
an old direct dependency cannot reinstall the package. Unity then resolves the
remaining packages. Review the resulting parent repository changes before
committing.

Removal verifies the checkout and its remote before changing files. Commit and
push local work first. Dirty or untracked files, unpublished commits, ambiguous
Git registration, nested submodules, and installed packages that depend on the
selected package block removal. The action does not force-delete local work.

Removal also works immediately after installation, before committing the parent
repository. The checkout is moved to a unique recovery folder under
`Library/SubmodulePackageOperations`, alongside manifest and registration
backups. Its `operation.txt` records the retained Git object directory. These
files are no longer an installed package or a registered submodule. Retain
them until the operation is reviewed; cleaning the project's Library folder
also removes these checkout backups.

Ordinary UPM package actions stay with Unity. Use Git for submodule updates and
source conversion.

Deletion protection also applies to Git checkouts whose embedded folder name
differs from the package's declared name. The guard rechecks local metadata
before allowing removal, so a recent Git change cannot rely on an older status
result. An unverified result blocks deletion until status can be read again.

After an external Git change, wait for Unity's import to finish and refresh
Package Manager. A submodule must be initialized and available as an installed
Unity package before the page can display it. See
[team clone setup](installation.md#team-clone-setup) for initialization commands.

## Background Work

Closing Package Manager stops status observation and attachment work. It
cancels an active local read and releases its update callback after that read
drains. While open, it checks local Git metadata every two seconds and responds
to package registration changes.
Ordinary asset edits do not launch Git status reads. Git-only changes reuse
Unity's installed-package snapshot; registration changes, reopening, and explicit
refresh capture it again. Unchanged Git metadata can reuse validated local
results. An unchanged status keeps the existing package view.
Index reads examine direct package entries rather than every tracked file inside
embedded packages, so large embedded source trees do not consume the status
output limit.

The Repository link reuses that installed-state snapshot. It adds no startup
scans, remote requests, Git commands, or polling. The extension performs no
background network discovery.
Its sidebar icons use small imports, and its native integration checks are
cached until code reload. Repeated membership and Repository-link checks reuse
derived source URLs within the accepted snapshot. A changed source or installed
identity still requires current matching metadata.

## Play Mode Without Domain Reload

Git Submodule Manager supports entering Play mode with domain reload disabled,
including when scene reload is disabled. Package Manager status, native hooks,
and removal protection belong to the Editor and remain active across Play mode
cycles. The extension does not reinstall its hooks or restart status observation
just because Play mode changes.

Script recompilation still rebuilds the integration from current installed state.
On Unity 6.5 and newer this uses Unity's code lifecycle callbacks; Unity 6.3 uses
the compatible assembly-reload callbacks. This package adds no gameplay state
and does not change the project's Enter Play Mode settings.

## Moving from Legacy to Main

The former remote catalogue, install and conversion actions, dependency planning,
Welcome window, and Preferences page are preserved on
[`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).
Published `0.8.1` artifacts also contain that earlier workflow. On `main`, the
manager reads installed state and provides direct link installation and safe
removal. It does not restore the legacy catalogue or conversion workflows.
Existing package installations remain in the project when you switch.

Finish any operation in the earlier implementation before switching branches.
If an earlier operation left recovery files, retain them until you have inspected
the project with Git. The current implementation does not resume the earlier
mutation workflow.
