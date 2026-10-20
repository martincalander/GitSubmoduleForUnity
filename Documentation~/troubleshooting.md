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

Use Git to remove that submodule. The extension prevents Unity's embedded-folder
removal from deleting a submodule directory. It does not remove Git registration,
convert package sources, or discard local changes.

Review the submodule's work and the parent repository's resulting changes in
your Git client before committing.

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
