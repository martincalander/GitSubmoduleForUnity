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

The package launches `git` and optional `gh` processes with
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
- missing CLI tools are never installed by the package; setup surfaces link only
  to the official Git and GitHub CLI installation guidance, and the package
  never runs a downloaded install script or user-provided command.

### Credentials

The package does not collect, persist, log, or forward tokens, passwords, SSH
keys, or credential-helper output. Authentication belongs to Git, the platform's
credential manager, SSH agent, and GitHub CLI.

### Network access

- Git performs clone, fetch, remote branch, and submodule operations.
- GitHub CLI performs authenticated repository discovery and retrieves root
  package metadata. Catalogue entries require a valid root `package.json` and
  matching `package.json.meta` from the same commit.
- The package does not contain its own HTTP client or telemetry.

### Filesystem access

The package reads project `.gitmodules` and package metadata. Submodule
filesystem changes are constrained to validated direct children of `Packages/`.
Persisted submodule URLs, local Git configuration, worktree origins, and
postconditions are revalidated around mutations. Catalogue entries and
read-only installs require bounded, strict-UTF-8 root `package.json` and
`package.json.meta` blobs from the same commit. Local package metadata must be
regular files rather than symbolic links or reparse points.

## User Responsibilities

- Install Git and GitHub CLI from trusted official sources.
- Review repository URLs before adding packages.
- Apply least-privilege access to private repositories.
- Protect developer and CI credentials outside Unity.
- Review Git changes before committing submodule updates or removals.
- Keep Unity, Git, GitHub CLI, and this package updated.
