# Security Policy

## Supported Versions

Security fixes are applied to the latest 0.8.x release and the current `main`
branch.

| Version or ref | Supported |
| --- | --- |
| 0.8.x | Yes |
| `main` | Yes |
| 0.7.x and earlier | No |

## Report a Vulnerability

Do **not** open a public issue or discussion.

Email [martin.calander@gmail.com](mailto:martin.calander@gmail.com) with:

- a description and expected impact;
- affected versions or commits;
- reproducible steps or a minimal proof of concept;
- relevant operating system, Unity, Git, and GitHub CLI versions;
- suggested remediation, if available.

Do not include real credentials or third-party private repository data. Do not
email sensitive attachments; ask for a secure exchange method first.

I aim to acknowledge reports within 72 hours and provide an initial assessment
within seven days. Disclosure timing will be coordinated around a fix and
release.

## Security Model

### Command execution

The package launches local read-only `git` commands with
`UseShellExecute = false`. It does not execute user input through a shell.

- repository URLs, branch names, package names, and managed paths are validated;
- network repositories are limited to HTTPS and SSH; plaintext `http://`,
  `git://`, embedded credentials, and executable remote helpers are rejected;
- submodule filesystem changes are restricted to validated direct
  `Packages/<reverse-domain-name>` children;
- stdout and stderr are redirected, drained concurrently, bounded, and treated
  as unusable for structural parsing when incomplete;
- commands have bounded timeouts;
- interactive credential prompts are disabled;
- missing CLI tools are never installed by the package; it never runs a
  downloaded install script or user-provided command.

### Credentials and Network Access

The simplified branch does not collect credentials, contact GitHub, run GitHub
CLI, or perform remote Git operations. It reads installed package information
and local Git metadata. Unity and Git retain ownership of package installation
and authentication outside this extension.

### Filesystem Access

The package reads the project index and `.gitmodules` through bounded local Git
commands and compares those records with Unity's registered package paths.
Classification requires a matching direct `Packages/<package-name>` path,
a stage-zero gitlink, and an unambiguous submodule registration. It is not a
claim that the checkout is clean or safe to delete.

The extension performs no repository or manifest writes. It blocks Unity's raw
embedded-folder deletion for known submodules, local Git checkouts, and direct
package paths whose status is pending or unverified. Existing recovery files
from earlier versions remain outside this extension's ownership.

## User Responsibilities

- Install Git from a trusted official source.
- Review repository URLs before adding packages.
- Apply least-privilege access to private repositories.
- Protect developer and CI credentials outside Unity.
- Review Git changes before committing submodule updates or removals.
- Keep Unity, Git, and this package updated.
