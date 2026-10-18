# Contributing to Git Submodule Manager

Contributions are welcome under the [MIT License](../LICENSE.md) and must follow
the [Code of Conduct](CODE_OF_CONDUCT.md).

The `simplify-package-status` branch focuses on installed-package status and a
native GitHub source page. Changes should make that view clearer or more
reliable. Installation, conversion, dependency planning, remote discovery, and
removal workflows are outside this experiment.

## Development Prerequisites

- Exact Unity `6000.3.22f1`, a Unity `6000.5.*f1` final release, exact
  `6000.6.0f1` or `6000.6.2f1`, exact `6000.7.0b1` or `6000.7.0b2` beta, or exact
  `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, or `7000.0.0a10` alpha.
- Git for preparing test repositories and managing the checkout.
- Python 3 and Node.js for repository, Markdown, and packaging checks.

The simplified implementation passed local validation on Unity `7000.0.0a10`
on macOS, including all 133 EditMode tests. Existing version gates do not imply that this branch has been
retested across all supported targets. GitHub CLI is not required.

## Set Up a Test Project

Use a working clone below `Packages/com.martincalander.gitsubmodulemanager`, or
load its `package.json` with **+ > Install package from disk...**. Open
**Window > Package Management > Package Manager** and inspect package status and
**Sources > GitHub**.

Include an installed GitHub submodule, an ordinary embedded package, and a UPM
Git dependency when checking classification. Let Unity generate metadata for new
assets. Do not edit generated solution or project files.

## Architecture and Scope

Read [Documentation~/architecture.md](../Documentation~/architecture.md) before
changing classification, reflection, Harmony hooks, or lifecycle behavior.

- Keep all product code under `Editor/` and tests under `Tests/Editor/`.
- Read local installed state without modifying repositories, manifests, or
  lockfiles.
- Preserve native Package Manager pages and ordinary package actions.
- Keep the guard against raw embedded-folder deletion of submodules.
- Validate reflection seams independently and fail closed when contracts change.
- Preserve the audited Mono and CoreCLR patching artifacts and their imports.
- Cover classification boundaries and protection against accidental deletion
  with focused regression tests.

## Test Before Opening a Pull Request

Run the applicable checks from the package root:

```bash
python3 .github/scripts/validate_repository.py
npx --yes markdownlint-cli2@0.23.0 "**/*.md" "#Library" "#Temp"
npm pack --ignore-scripts --dry-run
```

For C# changes, use Unity's official `unity` CLI from the exact test-project
root. Reuse the running Editor, discover its available commands and schemas,
refresh affected package paths, request a fresh compilation, and wait through
reload. Inspect nested results and the Console before running focused EditMode
tests. A successful `dotnet build` is not a Unity compilation.

Reflection or Harmony changes require the `PackageManagerCompatibility`
category. Broad lifecycle changes require the full
`MartinCalander.GitSubmoduleManager.Editor.Tests` assembly after focused tests
pass. Run that full assembly once at final verification and repeat it only when
a relevant change invalidates the result.

From a project with the official CLI and Pipeline connected, a compatibility
run may use the following commands after checking the current command schemas:

```bash
UNITY_PROJECT=/absolute/path/to/the/unity-project
unity --format json command run_tests \
  --mode editor \
  --filter PackageManagerCompatibility \
  --filter_type category \
  --async_tests true \
  --project-path "$UNITY_PROJECT"
unity --format json command test_status \
  --project-path "$UNITY_PROJECT"
```

Inspect package status, the GitHub source list, ordinary package actions, and
submodule deletion protection in Package Manager. Check refresh and script
reload. Record the exact Unity version, platform, test selection, and skipped
checks in the pull request. A local result does not establish release evidence
for another commit, artifact, Unity version, or platform.

CI runs license-free structure, Markdown, archive, and portability checks on
pull requests and pushes to `main`. Hosted workflows do not launch Unity or run
EditMode tests. Branch protection requires **Required sanity gate**, including
the Linux, macOS, and Windows portability matrix.

## Pull Request Checklist

- Keep one logical change per pull request.
- Explain the visible behavior and relevant validation.
- Include regression tests for changed classification or safety behavior.
- Update documentation and `CHANGELOG.md` for user-visible changes.
- Include before/after images for visual changes.
- Report untested Unity versions and platforms.
- Exclude credentials, generated project files, and build outputs.
- Confirm the contribution may be distributed under MIT.

## Commit Style

Use short imperative summaries, for example:

```text
Recognize installed GitHub packages
Preserve ordinary embedded package actions
Cover submodule status after refresh
```

## Reporting Security Problems

Do not open a public issue for a vulnerability. Follow [SECURITY.md](SECURITY.md).

## Attribution

Git Submodule Manager was created by Martin Calander. Contributors retain
authorship of their contributions; the combined project is distributed under
the [MIT License](../LICENSE.md).

## Experiment Verification on 2026-10-18

The local `simplify-package-status` checkout was tested with Unity CLI
`1.0.0-beta.13`, Pipeline `0.8.0-exp.1`, and Unity `7000.0.0a10` on macOS.

- Fresh Unity compilation completed without compiler errors.
- All 84 `PackageManagerCompatibility` checks passed.
- All 42 focused installed-status tests passed.
- All 133 tests in the final Editor test assembly passed, with no skipped or
  inconclusive tests. This includes two Play mode cycles with domain reload
  disabled and scene reload enabled, with the original settings restored.
- The live GitHub page contained the two installed workspace submodules with
  **Submodule** labels and PlayerPrefs Editor with Unity's normal **Git** label.
- Repository validation, 13 Python repository tests, Markdown lint, package
  dry run, and whitespace checks passed.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux. No player build was
needed for this Editor-only change. Signed release archives and protected CI
checks were not run. Local validation is not release evidence for another
commit, Unity version, archive, or platform.
