# Security Policy

## Supported Versions

Security fixes are applied to the latest released major version and the current
`main` branch.

| Version | Supported |
| --- | --- |
| 1.x | Yes |
| 0.x | No |

## Report a Vulnerability

Do **not** open a public issue or discussion.

Email [martin.calander@gmail.com](mailto:martin.calander@gmail.com) with:

- a description and expected impact;
- affected versions or commits;
- reproducible steps or a minimal proof of concept;
- relevant operating system, Unity, Git, and GitHub CLI versions;
- suggested remediation, if available.

Do not include real credentials or third-party private repository data. Encrypt
sensitive attachments before sending and request a secure exchange method when
needed.

You can expect acknowledgment within 72 hours and an initial assessment within
seven days. Disclosure timing will be coordinated around a fix and release.

## Security Model

### Command execution

The package launches `git` and optional `gh` processes with
`UseShellExecute = false`. It does not execute user input through a shell.

- repository URLs, branch names, package names, and managed paths are validated;
- mutations are restricted to direct `Packages/com.author.package` paths;
- stdout and stderr are redirected and drained concurrently;
- commands have bounded timeouts;
- interactive credential prompts are disabled;
- missing CLI tools are never installed silently: supported native installer
  commands are shown verbatim and require explicit confirmation;
- install assistance is limited to fixed Git and GitHub CLI package identifiers
  and never runs a downloaded install script or user-provided command.

### Credentials

The package does not collect, persist, log, or forward tokens, passwords, SSH
keys, or credential-helper output. Authentication belongs to Git, the platform's
credential manager, SSH agent, and GitHub CLI.

### Network access

- Git performs clone, fetch, remote branch, and submodule operations.
- GitHub CLI performs authenticated repository discovery and root
  `package.json` checks.
- The package does not contain its own HTTP client or telemetry.

### Filesystem access

The package reads project `.gitmodules` and package metadata. Mutating package
operations are constrained to validated direct children of `Packages/`.

## User Responsibilities

- Install Git and GitHub CLI from trusted official sources.
- Review repository URLs before adding packages.
- Apply least-privilege access to private repositories.
- Protect developer and CI credentials outside Unity.
- Review Git changes before committing submodule updates or removals.
- Keep Unity, Git, GitHub CLI, and this package updated.
