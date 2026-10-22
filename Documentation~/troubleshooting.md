# Troubleshooting

## The GitHub Source Is Missing

Check the exact Unity version against the
[compatibility list](installation.md#unity). The native integration depends on
guarded Package Manager internals. GitHub belongs under **Sources**. An
unsupported version or a changed contract disables the affected integration
instead of replacing Unity's normal pages.

Current source accepts all canonical `7000.0.0a*` alpha revisions with a positive
integer suffix. If a newer alpha is rejected by number alone, check that the
installed package contains this policy; older revisions use an exact alpha list.
Passing the version gate still requires the native API checks to succeed.

Check the Unity Console for compilation or compatibility diagnostics and reopen
Package Manager after scripts finish compiling.

## The GitHub Page Is Empty

The page shows installed GitHub packages only. It does not browse your account,
organizations, or remote repositories. Install a package using Unity or Git,
wait for Unity to resolve it, and refresh Package Manager.

For a submodule, confirm that it is initialized and that Unity recognises its
root `package.json` as an installed package. GitHub CLI authentication has no
effect on this view.

## A Package Is Not Labelled Submodule

An embedded folder alone is not enough to identify a package as a submodule.
Check the project's Git registration and package location:

```bash
git status
git submodule status
```

Use a direct package folder such as `Packages/com.author.package` and confirm
that Unity resolves that same folder. An incomplete, ambiguous, or unsupported
registration may remain classified by Unity's normal source. Check these
conditions:

- A Git index entry without a `.gitmodules` registration: inspect the package
  path and URL in `.gitmodules`.
- A `.gitmodules` registration without a Git index entry: inspect the parent
  repository's staged changes and submodule entry.
- A package folder or metadata path that does not match: compare the folder,
  `package.json` name, `.gitmodules` path, and Git index path, including spelling.
- A failed metadata read: check the Console and confirm that Git is
  available and the Unity project is the parent repository's root.

Review the existing state before making changes with your Git client. The
extension does not create missing registrations or repair paths automatically.

A local package or UPM Git dependency can contain Git metadata without being a
submodule of this project. The extension must not label it as a submodule on
that basis alone.

## Git Changed a Package but the View Is Stale

Wait for import and package resolution to finish, then refresh Package Manager.
If the package itself is unavailable, fix the Git checkout or package manifest
first. The extension does not initialize submodules or resolve missing
submodule files for you.

## The Repository Link Is Missing

**Repository** appears first in Unity's Documentation, Changelog, and Licenses
row when a safe browser URL can be derived for the installed selection. Select
the installed version, wait for package resolution, and refresh Package Manager
if the selection no longer matches Unity's installed record.

Relative, local, and unknown SSH sources have no guessed browser destination.
Unavailable or unverified source metadata also leaves the link absent. The
extension does not query the network to find a missing repository URL. Check the
Console for compatibility diagnostics if a supported URL has no link.

## Remove Is Blocked for a Submodule

Package Manager **Remove** handles verified submodules through a Git-aware
removal action. Commit and push local work, remove untracked files only after
reviewing them, and resolve any Git registration mismatch before retrying.
An unavailable remote or nested submodule also blocks removal.

Git's `assume-unchanged` and `skip-worktree` flags can hide modified files.
Clear those flags and review the actual changes before removing the package.
Stage or restore `.gitmodules` changes before starting an operation. Shared or
misdirected Git storage must be reviewed with Git rather than removed through
Package Manager.

If native integration is unavailable, the extension continues to block raw
embedded-folder deletion. Use Git after reviewing the checkout in that case.
The action never converts sources or discards local changes.

Review the submodule's work and the parent repository's resulting changes in
your Git client before committing.

## An Install or Removal Did Not Finish

Read the error's recovery path before retrying. Current operations retain files
under `Library/SubmodulePackageOperations`. A removal keeps `operation.txt`,
manifest and `.gitmodules` backups, and the moved checkout. The Git object
storage remains in the parent repository. Successful removal retains these
backups too, but the checkout is no longer installed or registered.

Inspect `git status`, `.gitmodules`, `Packages/manifest.json`, and the recovery
record together. Do not delete recovery evidence or overwrite an occupied
package path to force a retry. Review any partial changes with your Git tools.

For install failures, verify that the supplied repository has `package.json`
at its root, with a lowercase reverse-domain name, a semantic version, and valid
package dependencies. Use the repository URL and optionally `#branch`; package
subfolder URLs and tag or commit pins are not supported by this action.

## Import Worker Errors When Removing This Package

Cold-project self-removal on Unity `6000.5.5f1` reproduced import-worker crashes
while reading a removed assembly definition. The main Editor completed removal,
unloaded the package, released its locks, and retained recovery files. The error
matches Unity's documented
[UUM-119732](https://issuetracker.unity.com/issues/21204/asset-import-workers-crash-when-asmdef-is-deleted).
Unity `6000.7.0b2` instead reported recoverable worker restarts, and
`7000.0.0a11` completed the same check without worker errors.

Native deletion and a main-thread move control did not reproduce the error in
their checked runs. The operation's timing may contribute; it has not been
isolated, and changing refresh order did not resolve it. These results do not
establish clean self-removal on every supported Editor.

If this occurs, let Unity finish resolving and importing, then check Package
Manager and Git state before retrying. Preserve the recovery folder if removal
did not finish.

## Recovery Files from an Earlier Version

The current implementation on `main` does not resume legacy installs,
conversions, or removals. Inspect
`git status`, `.gitmodules`, `Packages/manifest.json`, and any retained files
under `Library/GitSubmoduleManager/Recovery` before continuing a previously
interrupted operation. Keep recovery evidence until its contents have been
accounted for.

## A Teammate Cannot Clone a Private Package

Access to the parent repository does not grant access to its private submodules.
Give the teammate or CI identity access to every private package repository and
configure Git credentials before initializing submodules. The GitHub source
view does not manage credentials.

## Reporting a Problem

Follow the [support guide](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SUPPORT.md).
Include the exact Unity version, operating system, package commit, expected
package status, actual status, and sanitized Console output. State whether you
are using `main`, `legacy`, or a published release. Published `0.8.1` artifacts
contain the legacy management workflow.
