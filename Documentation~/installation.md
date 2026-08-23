# Installation and Compatibility

## Prerequisites

### Unity

Unity `6000.5.*f1` final releases are supported. The package manifest
declares `6000.5.0f1` as its minimum Editor version. All package assemblies are
editor-only.

### Git CLI

Git is required. Confirm it is visible to your user account:

```bash
git --version
```

Official downloads are available at [git-scm.com](https://git-scm.com/downloads).
If Git is missing, the standalone Welcome window links to the official download
page. Install it outside Unity, then choose **Check Again**.

### GitHub CLI

GitHub CLI is optional but recommended. It enables authenticated user and
organization discovery plus remote `package.json` validation.

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

## First Open

On Unity `6000.5.*f1`, open **Window > Package Management > Package Manager** and
select **GitHub** under **Sources**. The native package list incrementally
adds valid UPM packages discovered from every repository page owned by the
authenticated user and their visible organizations, alongside installed GitHub
submodules. Native search, sorting, selection, and details work across the
combined list. Select a discovered package, use **Repository** to review its
website, select a branch, then open **Install** and choose **Install as Git
Submodule** or **Install as Read-Only Package**. Choose **Refresh** to rescan.

If GitHub CLI is missing or authentication fails, installed GitHub submodules
remain visible, and direct URL installation remains available from Package
Manager's **+ > Install package as Git Submodule...** command.

The first activation of **Sources > GitHub** shows a small standalone Welcome
window that checks Git, GitHub CLI, and GitHub authentication for the current
user. Its shown flag is stored per user and project under Unity's ignored
`UserSettings/` directory. Reopen it with **Show Welcome** under Unity's
**Preferences > Git Submodule Manager** page.

That Preferences page repeats the Welcome setup checks with installed versions,
authentication status, official install/help actions, and **Check Again**. It
also provides **Open GitHub Package Manager** and stores the following per-user
defaults and safety choices:

- initial repository visibility (**All Repositories** by default) and
  organization filters (blank for all owners by default);
- initial discovered-package install mode (**Git Submodule** by default);
- whether a complete, unambiguous missing-dependency plan may proceed without
  another prompt;
- whether the second confirmation may be skipped for a clean, routine
  submodule removal or conversion.

Both confirmation-suppression choices are off by default. Dirty, unpushed,
changed, or unverified-work warnings are safety checks and are never suppressed.

## Add the Package with UPM

Use **Window > Package Management > Package Manager > + > Install package from
git URL...**:

```text
https://github.com/martincalander/GitSubmoduleManager.git
```

To install a specific released version, append a Git tag:

```text
https://github.com/martincalander/GitSubmoduleManager.git#<version-tag>
```

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

The supported Editor line is Unity `6000.5.*f1`. Unity package manifests express
minimum versions rather than wildcard or maximum ranges, so `package.json` uses
`unity: 6000.5` with `unityRelease: 0f1` to declare the minimum
`6000.5.0f1`; this support statement limits the currently validated line to
Unity `6000.5.*f1` final releases.

| Platform | CLI discovery locations |
| --- | --- |
| Windows | `PATH`, Git for Windows, GitHub CLI program directories |
| macOS | `PATH`, Homebrew on Apple Silicon and Intel, system paths |
| Linux | `PATH`, common system paths, `/snap/bin` |

The package uses `System.Diagnostics.Process` without a shell, so command
arguments follow the platform's normal process rules. Paths are normalized for
Git configuration while Windows backslashes are preserved when quoting local
repository locations.

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

The rename changes the UPM package, assembly, namespace, and legacy public
window type identities. Existing Git URL installations must replace the
dependency key in `Packages/manifest.json`; changing only the tag is not
sufficient:

```json
{
  "dependencies": {
    "com.martincalander.gitsubmodulemanager": "https://github.com/martincalander/GitSubmoduleManager.git#<renamed-release-tag>"
  }
}
```

Remove the old `com.martincalander.gitpackagemanager` key and let Unity
regenerate `Packages/packages-lock.json`. Replace `<renamed-release-tag>` with a
published tag that includes the Git Submodule Manager identity; the older
`v1.0.0` release predates this rename.

For a submodule installation, also coordinate the parent repository's gitlink
and `.gitmodules` path from
`Packages/com.martincalander.gitpackagemanager` to
`Packages/com.martincalander.gitsubmodulemanager`, then run
`git submodule sync --recursive`. Update downstream assembly definition
references from `MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`, and source namespaces from
`MartinCalander.GitPackageManager.Editor` to
`MartinCalander.GitSubmoduleManager.Editor`.

The former public management-window redirect and package menu were removed.
Current workflows use Unity's native Package Manager surface; integrations
must not depend on the deleted window type.

Serialized editor types carry Unity migration metadata. Per-user preferences
are copied non-destructively to `UserSettings/GitSubmoduleManagerSettings.asset`,
and interrupted-operation state under the legacy Library and SessionState paths
remains recoverable.

After this one-time identity migration, a dependency pinned to a Git tag can be
upgraded by changing its tag normally.
