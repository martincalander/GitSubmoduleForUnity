# Security Policy

## Supported Versions

Security fixes are applied to the latest 0.8.x release and the current `main`
branch.

| Version or ref | Supported |
| --- | --- |
| 0.8.x | Yes |
| `main` | Yes |
| 0.7.x and earlier | No |

The [`legacy` branch](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy)
archives the former manager with installation, conversion, removal, and remote
discovery features. Its security model is documented in that branch. This
archive does not change the supported-release policy above.

## Report a Vulnerability

Do **not** open a public issue or discussion.

Email [martin.calander@gmail.com](mailto:martin.calander@gmail.com) with:

- a description and expected impact;
- affected versions or commits, including whether the code comes from `main`
  or `legacy`;
- reproducible steps or a minimal proof of concept;
- relevant operating system, Unity, and Git versions; include GitHub CLI only
  for a legacy discovery issue;
- suggested remediation, if available.

Do not include real credentials or third-party private repository data. Do not
email sensitive attachments; ask for a secure exchange method first.

I aim to acknowledge reports within 72 hours and provide an initial assessment
within seven days. Disclosure timing will be coordinated around a fix and
release.

## Security Model

This section describes the status and sources extension on `main`.

### Command execution

The package launches local read-only `git` commands with
`UseShellExecute = false` and fixed arguments. Repository data is never used as
a command or evaluated through a shell.

- commands read the project root, index location, package gitlinks, and local
  `.gitmodules` registrations;
- `.gitmodules` includes are disabled, and linked or oversized `.gitmodules`
  files are rejected;
- inherited `GIT_*` environment variables are cleared, optional Git locks are
  disabled, and interactive credential prompts are disabled;
- stdout and stderr are redirected, drained concurrently, bounded, and treated
  as unusable for structural parsing when incomplete;
- commands have bounded timeouts;
- missing CLI tools are never installed by the package; it never runs a
  downloaded install script or user-provided command.

### Credentials and Network Access

The extension does not collect credentials, contact GitHub, run GitHub CLI, or
perform remote Git operations. It reads installed package information and
local Git metadata. A GitHub source label describes a locally recorded source;
it does not verify a remote repository or authorize a network connection.
Unity and Git retain ownership of package installation and authentication
outside this extension.

### Filesystem Access

The package reads the project index and `.gitmodules` through bounded local Git
commands and compares those records with Unity's registered package paths.
Classification requires a matching direct `Packages/<package-name>` path,
a stage-zero gitlink, and an unambiguous submodule registration. It is not a
claim that the checkout is clean or safe to delete.

The extension performs no repository or manifest writes. For direct children
of `Packages/`, it blocks Unity's raw embedded-folder deletion when the path
has a gitlink, submodule registration, or local `.git` file or directory. It
also blocks that deletion while status is pending, unavailable, or stale.
The guard compares normalized paths independently of the package name, so a
folder with a different name still receives protection. Existing recovery
files from earlier versions remain outside this extension's ownership.

## User Responsibilities

- Install Git from a trusted official source.
- Review repository URLs before adding packages.
- Apply least-privilege access to private repositories.
- Protect developer and CI credentials outside Unity.
- Review Git changes before committing submodule updates or removals.
- Keep Unity, Git, and this package updated.
