# Contributing to Git Package Manager

Thank you for helping make Git Package Manager more reliable for Unity teams.
Contributions are welcome under the [MIT License](../LICENSE.md) and must follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Good Contributions

The project especially values:

- reproducible Windows, macOS, or Linux fixes;
- compatibility improvements across supported Unity versions;
- failure handling and data-loss prevention;
- focused tests for Git and GitHub CLI edge cases;
- accessibility and native-editor UI improvements;
- concise documentation and diagnostics.

Discuss large features or architecture changes in an issue before investing in
an implementation. The project intentionally keeps a narrow submodule-only
scope.

## Development Prerequisites

- Unity 2021.3 or newer;
- Git CLI;
- GitHub CLI for discovery testing;
- Python 3 for repository sanity checks.

## Set Up a Test Project

Add a fork or working clone below a Unity project's `Packages/` directory:

```bash
git clone https://github.com/<your-user>/GitPackageManager.git \
  Packages/com.martincalander.gitpackagemanager
```

Open the Unity project, then open **Window > Package Management > Git Package
Manager**.

Do not edit Unity-generated solution or project files. Let Unity import new
package files and generate `.meta` files.

## Architecture

```text
Editor/
├── PackageManagerWindow.*      editor UI and actions
├── DiscoveryCoordinator.cs    paged GitHub state
├── RepositoryCoordinator.cs   lazy branch loading
├── Models/                    internal data objects
└── Utilities/
    ├── CliCommandRunner.cs    bounded process execution
    ├── CliInstaller.cs        install guidance
    ├── GitUtility.cs          Git submodule operations
    └── GitHubUtility.cs       GitHub CLI operations
```

Read [Documentation~/architecture.md](../Documentation~/architecture.md) before
changing command execution, package mutation, threading, discovery, or lifecycle
behavior.

## Engineering Rules

- Keep runtime code out of the package; this is an editor-only tool.
- Preserve Windows, macOS, and Linux behavior.
- Never evaluate user input through a shell.
- Keep mutations restricted to direct `Packages/com.author.package` paths.
- Do not store credentials or run installers without the user's explicit
  confirmation.
- Avoid implicit network or repository mutations during editor startup.
- Prefer explicit state, actionable errors, and rollback over optimistic UI.
- Keep public APIs minimal; most implementation types should remain `internal`.
- Add tests for parsing, state transitions, validation, and regressions.

## Test Before Opening a Pull Request

Run the license-free repository checks:

```bash
python3 .github/scripts/validate_repository.py
npx --yes markdownlint-cli2@0.23.0 "**/*.md" "#Library" "#Temp"
npm pack --dry-run
```

In Unity:

1. verify the package compiles without warnings or errors;
2. run `MartinCalander.GitPackageManager.Editor.Tests` in EditMode;
3. exercise the changed workflow manually;
4. inspect the Console for new warnings or errors;
5. test the relevant CLI-missing, authentication, or failure state;
6. test on every operating system affected by the change when possible.

If a platform or Unity version could not be tested, state that clearly in the
pull request.

CI runs license-free structure, Markdown, archive, and portability checks on
every pull request. Unity credentials are never exposed to pull-request code.
After reviewing a contribution, a maintainer can manually dispatch the
**Sanity Checks** workflow with the reviewed commit as its `ref` to run the
protected Unity compile and EditMode-test gate.

## Pull Request Checklist

- Keep one logical change per pull request.
- Use a descriptive title and explain user-visible behavior.
- Link the related issue when one exists.
- Include tests or explain why the change is documentation-only.
- Update documentation and `CHANGELOG.md` for user-visible changes.
- Include before/after images for visual changes.
- Do not commit secrets, credentials, generated Unity project files, or build
  outputs.
- Confirm the contribution may be distributed under MIT.

## Commit Style

Use short imperative summaries, for example:

```text
Prevent stale discovery results after owner changes
Document private submodule authentication
Add Windows path quoting regression test
```

Conventional Commits are welcome but not required.

## Reporting Security Problems

Do not open a public issue for a vulnerability. Follow
[SECURITY.md](SECURITY.md).

## Attribution

Git Package Manager was created by Martin Calander. Contributors retain
authorship of their contributions; the combined project is distributed under
the [MIT License](../LICENSE.md).
