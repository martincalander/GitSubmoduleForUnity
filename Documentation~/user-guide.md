# User Guide

Open **Window > Package Management > Package Manager**. The simplified manager
adds installed-package status to that window and a **GitHub** page under
**Sources**. See [compatibility](installation.md#unity) for the exact Editor
targets retained by this experimental branch.

## Package Status

An installed package recognised as a submodule is labelled **Submodule** in the
package list. Select a package to see its installation source and submodule
status in the details area. Other packages retain their Unity installation
source, such as a registry, Git dependency, or local package.

While installed state is loading, the details area reports that the status is
being checked. A failed read reports unavailable status rather than claiming
that the package is not installed.

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

Opening this page does not query GitHub, read account credentials, or require
GitHub CLI. Private installed packages appear from their local source metadata.
Their visibility does not grant access to teammates or build machines.

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

The extension performs no background network discovery. Its sidebar icons use
small imports, and its native integration checks are cached until code reload.

## Switching from the Earlier Workflow

This branch removes the manager's remote catalogue, install and conversion
actions, dependency planning, Welcome window, and Preferences page. Existing
package installations remain in the project. The simplified view reads their
current state and leaves package changes to Unity and Git.

Finish any operation in the earlier implementation before switching branches.
If an earlier operation left recovery files, retain them until you have inspected
the project with Git. This branch does not resume the earlier mutation workflow.
