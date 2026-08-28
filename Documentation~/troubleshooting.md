# Troubleshooting

## Git Is Not Detected

Run:

```bash
git --version
```

If that succeeds in a terminal but not in Unity, restart Unity after installing
Git. GUI applications can inherit a different `PATH` than interactive shells.
The package also probes common installation directories on Windows, macOS, and
Linux.

If the installer finishes but the tool remains unavailable, click **Check
again**. Restart Unity if the operating system updated the GUI application's
environment only after launch.

## GitHub Discovery Is Disabled

Run:

```bash
gh --version
gh api user --hostname github.com --jq .login
```

If authentication fails, open **Preferences > Git Submodule Manager > Show
Welcome**, copy the authentication command, or run `gh auth login --hostname
github.com --web` in a visible terminal.
Ensure the active account can see the user or organization repositories you
expect. Until authentication is restored, **Sources > GitHub** continues to show
installed GitHub packages. Installed-package actions remain in Package Manager,
and **+ > Install package as Git Submodule...** remains available for direct URL
installation. After fixing GitHub CLI or authentication, choose **Refresh** in
Package Manager to start a new catalogue scan.

If Unity reports that `GH_TOKEN` or `GITHUB_TOKEN` controls GitHub CLI, the
copied authentication command cannot replace that environment-provided identity.
Remove or update the variable, restart Unity, and click **Check Again**, or run
the command from a terminal where the variable is unset.

## A Repository URL Is Rejected

Use HTTPS or SSH for network repositories. Plaintext `http://` and `git://`
transports are blocked because they cannot protect package code from
substitution in transit. Explicit local paths and `file://` repositories are
supported. Do not place passwords or access tokens in a URL; configure Git's
credential manager or SSH agent in a normal terminal.

## A Command Times Out or Appears to Need Credentials

Git credential prompts are disabled inside Unity to prevent hidden interactive
prompts from freezing the editor workflow. Authenticate in a normal terminal,
verify a direct `git clone` or `git ls-remote`, then retry.

For SSH URLs, ensure the key is loaded into the platform's SSH agent. For HTTPS,
verify the configured Git credential manager.

## A Package Is Uninitialized

Initialize it with Git, then return to Package Manager and choose **Refresh**:

```bash
git submodule update --init --recursive -- Packages/com.author.package
```

## A Repository Is Not a Valid Package

The repository root must contain a valid `package.json` whose `name` matches the
destination package name, for example:

```json
{
  "name": "com.example.tool",
  "version": "1.0.0",
  "displayName": "Example Tool"
}
```

Automatic GitHub catalogue discovery also requires a regular root
`package.json.meta` containing `fileFormatVersion: 2` and exactly one nonzero
32-hexadecimal root GUID. Unity normally creates this file when the manifest is
tracked as an asset. The importer section may vary and is not used for
eligibility. A direct URL can still install an otherwise valid root UPM package
without this marker, but the installer shows a mandatory unverified-package
warning.

Monorepos that keep the UPM package below the repository root are not currently
supported by discovery or installation.

## A GitHub Package Is Missing from Sources

The native catalogue scans all repository pages owned by the authenticated user
and every organization visible to that account. Confirm that `gh api user`
returns the intended identity and that the identity can access the missing
repository. Organization membership or repository permissions may need to be
updated outside Unity.

Discovery publishes only repositories whose root `package.json` contains a
valid reverse-domain `name` and SemVer 2.0 `version` and whose sibling
`package.json.meta` has a valid Unity meta header and GUID. A missing, malformed,
unavailable, symlinked, oversized, or nested manifest or marker is intentionally
excluded. Correct the repository or access issue, then choose **Refresh** in
Package Manager to rescan the account and organizations.

## Dependency Resolution Says GitHub Coverage Is Incomplete

For package names outside `com.unity.*`, configured registries are searched only
after a successful scan of the authenticated user's repositories and every
visible organization proves that GitHub has no matching package. A failed owner
page, coverage warning, or unavailable `package.json` therefore blocks the root
install instead of guessing that registry fallback is safe.

Restore GitHub CLI authentication and repository access, correct the unavailable
manifest if you control it, then choose **Refresh** and retry. If GitHub does
contain the package, that source has priority; a duplicate match, version
mismatch, or incomplete repository identity must be corrected rather than
bypassed through a registry.

## An Installed Manifest Did Not Match Preflight

Dependency-aware installation compares the resulting root `package.json` with
the exact name, version, and dependency map inspected before mutation. For a
catalogue package, it also compares the resulting `package.json.meta` GUID with
the verified marker. When a branch changes during installation or Unity resolves
an unexpected direct Git entry, the new install is rejected and automatic
rollback or removal is attempted only when ownership is proven.

If the diagnostic says cleanup was incomplete, inspect `git status`, the package
path, and `Packages/manifest.json` before retrying. The mismatched checkout or
manifest entry may still be present.

## The Package Name Is Rejected

Managed names use lowercase reverse-domain form with at least three segments:

```text
com.author.package
```

Spaces, uppercase letters, path separators, and traversal segments are rejected.

## An Update Changed Files but Unity Still Shows the Old State

Wait for Unity's package import to finish, then choose **Refresh** in Package
Manager.

Confirm the parent repository records the new submodule commit with:

```bash
git status
git submodule status
```

## Unity Reports an Interrupted Repository Operation

Do not dismiss the recovery warning until you have inspected `git status`,
`.gitmodules`, the package path, and any Git or SSH processes that may still be
running. The warning is retained when process termination, rollback, or a
postcondition cannot be proven. Once the repository is safe, open **Preferences
> Git Submodule Manager**, review the retained warning again, choose
**Acknowledge Inspected Recovery State...**, and refresh Package Manager.

## A Teammate Cannot Clone a Private Package

Access to the parent repository does not grant access to its private
submodules. Give the teammate or CI identity access to every private package
repository and configure credentials before initializing submodules.

## Reporting a Problem

Follow the [support guide](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SUPPORT.md)
and include Unity version, operating system,
package commit, CLI versions, the exact operation, and complete sanitized error
text.
