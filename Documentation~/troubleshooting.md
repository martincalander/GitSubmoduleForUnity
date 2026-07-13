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
gh auth status -h github.com
```

If authentication fails, use `gh auth login`. Ensure the active account can see
the user or organization repositories you expect.

The editor can help install `gh`, but authentication always remains a separate
user action in a normal terminal.

## A Command Times Out or Appears to Need Credentials

Git credential prompts are disabled inside Unity to prevent hidden interactive
prompts from freezing the editor workflow. Authenticate in a normal terminal,
verify a direct `git clone` or `git ls-remote`, then retry.

For SSH URLs, ensure the key is loaded into the platform's SSH agent. For HTTPS,
verify the configured Git credential manager.

## A Package Is Uninitialized

Select the package and click **Initialize**, or run:

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

Monorepos that keep the UPM package below the repository root are not currently
supported by discovery or installation.

## The Package Name Is Rejected

Managed names use lowercase reverse-domain form with at least three segments:

```text
com.author.package
```

Spaces, uppercase letters, path separators, and traversal segments are rejected.

## An Update Changed Files but Unity Still Shows the Old State

Wait for Unity's package import to finish, then use the window's refresh action.
Confirm the parent repository records the new submodule commit with:

```bash
git status
git submodule status
```

## A Teammate Cannot Clone a Private Package

Access to the parent repository does not grant access to its private
submodules. Give the teammate or CI identity access to every private package
repository and configure credentials before initializing submodules.

## Reporting a Problem

Follow the [support guide](https://github.com/martincalander/com.martincalander.gitpackagemanager/blob/main/.github/SUPPORT.md)
and include Unity version, operating system,
package commit, CLI versions, the exact operation, and complete sanitized error
text.
