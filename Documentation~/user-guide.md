# User Guide

Open **Window > Package Management > Package Manager**. Git Submodule Manager
adds installed-package details, a **Submodules only** filter, and
**Sources > GitHub** to Unity's native window. This is the current design on `main`. See
[compatibility](installation.md#unity) for the exact Editor targets and local
verification limits.

## Package Status

An installed package recognised as a submodule is labelled **Submodule** in the
package list. Select a package to see its installation source and submodule
status in the details area. Other packages retain their Unity installation
source, such as a registry, Git dependency, or local package.

While installed state is loading, the details area reports that the status is
being checked. A failed read reports unavailable status rather than claiming
that the package is not installed. An unverified checkout explains what is
missing or inconsistent, such as a `.gitmodules` registration, a Git index
entry, or a matching package path. Follow that guidance to inspect the project;
the extension does not repair registration or change files.

Submodule status describes how the package is installed. It does not report
whether the checkout is clean, up to date, or pushed. Use your Git client for
those details.

## Submodules Filter

Choose **Project > All Packages** and enable **Submodules only** beside search.
Older Editors call this page **In Project**. The toggle filters the existing
list to packages verified as submodules of this project, across Git hosts,
including private hosts and relative submodule URLs. It uses the same cached
classification as the **Submodule** label. Unity's search and sorting continue
to apply to the filtered list.

Turn the toggle off to see the full list again. It applies only to this project
page and starts off after closing Package Manager or reloading scripts.
**Sources > GitHub** and other pages keep their own contents. There is no
separate Submodules sidebar page.

Ordinary UPM Git dependencies are not submodules and do not appear here. Neither
do uninitialized submodules, packages that Unity has not resolved, or checkouts
whose registration cannot be verified. Turn off the filter and select such a
package to inspect its status explanation.

## GitHub Source

Choose **Sources > GitHub** to see installed packages hosted on GitHub:

- package submodules registered by the project;
- UPM Git dependencies resolved by Unity.

The page uses Unity's native package list, search, sorting, and selection. It
contains installed packages only, so an empty page is expected in a project
without GitHub packages. Repositories that you own or can access do not appear
unless a package from them is already installed.

Opening this page does not query GitHub, read account credentials, or require
GitHub CLI. Private installed packages appear from their local source metadata.
Their visibility does not grant access to teammates or build machines.

## Source Details and Shortcuts

Select an installed package to inspect its source and resolved location. A
verified submodule's registered source URL appears as selectable text. Ordinary
UPM Git dependencies retain Unity's native **Installed From** field.
Available actions depend on the metadata for that exact installed version:

- **Open repository** opens a repository URL in your browser when one can be
  derived safely. HTTP and HTTPS sources can be opened directly; supported
  public Git hosts also have browser links for SSH sources. Relative, local,
  and unknown SSH sources have no guessed browser link.
- **Reveal package folder** opens the resolved package location in your system's
  file browser. For a UPM Git dependency this is Unity's cached package folder.

The extension omits embedded credentials and unrelated query parameters from
its source URLs. Browser links omit the package subdirectory and revision suffix
to open the repository itself. A relative submodule URL remains relative in the
displayed source.

These shortcuts do not edit the package or change its installation source.
Opening a private repository may require signing in through your browser.

## Git Revision Details

For an ordinary UPM Git dependency, the details show the requested revision next
to the installed commit, for example `main → 796ca8d`. **Copy installed commit**
copies the complete commit hash. If the dependency does not specify a revision,
the requested side says **default branch**; this does not identify the remote
branch's current name or tip.

Both values come from Unity's registered package metadata. Missing commit data
is shown as unavailable. The extension does not fetch a repository, check for
updates, or compare the installed commit with a remote branch. It also does not
read a submodule's checkout `HEAD` to present it as a UPM resolved revision.

## Changing Packages

Use Unity's Package Manager to add, update, or remove ordinary UPM packages.
Use Git to add, update, convert, or remove submodules and review the parent
repository's `.gitmodules` and gitlink changes.

The extension blocks Unity's raw embedded-package deletion for submodules.
That action would delete the package folder without completing Git's submodule
workflow. Remove a submodule with your Git tools after reviewing its local work.
The extension does not run removal or conversion on your behalf.

Deletion protection also applies to Git checkouts whose embedded folder name
differs from the package's declared name. The guard rechecks local metadata
before allowing removal, so a recent Git change cannot rely on an older status
result. An unverified result blocks deletion until status can be read again.

After an external Git change, wait for Unity's import to finish and refresh
Package Manager. A submodule must be initialized and available as an installed
Unity package before the page can display it. See
[team clone setup](installation.md#team-clone-setup) for initialization commands.

## Background Work

Closing Package Manager stops its status observation and attachment work after
any pending local read finishes. While it is open, the extension checks local
Git metadata every two seconds and responds to package registration changes.
Ordinary asset edits do not launch Git status reads. An unchanged result keeps
the existing package view instead of rebuilding it.

The submodule filter, source shortcuts, revision details, and status explanations
reuse that installed-state snapshot. They add no startup scans, remote requests,
Git commands, or polling. The extension performs no background network discovery.
Its sidebar icons use small imports, and its native integration checks are
cached until code reload.

## Moving from Legacy to Main

The former remote catalogue, install and conversion actions, dependency planning,
Welcome window, and Preferences page are preserved on
[`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).
Published `0.8.1` artifacts also contain that earlier workflow. On `main`, the
manager reads installed state and leaves package changes to Unity and Git.
Existing package installations remain in the project when you switch.

Finish any operation in the earlier implementation before switching branches.
If an earlier operation left recovery files, retain them until you have inspected
the project with Git. The current implementation does not resume the earlier
mutation workflow.
