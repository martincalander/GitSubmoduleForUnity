# Contributing to Git Submodule Manager

Contributions are welcome under the [MIT License](../LICENSE.md) and must follow
the [Code of Conduct](CODE_OF_CONDUCT.md).

The `main` branch develops installed-package status and a native GitHub source
page with a small Editor footprint. Changes should make that view clearer, more
reliable, or less expensive to run. Installation, conversion, dependency
planning, remote discovery, and removal workflows are outside the current
product scope. The former implementation is preserved on
[`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy);
published `0.8.1` artifacts still contain that implementation.

## Development Prerequisites

- Exact Unity `6000.3.22f1`, a Unity `6000.5.*f1` final release, exact
  `6000.6.0f1` or `6000.6.2f1`, exact `6000.7.0b1` or `6000.7.0b2` beta, or exact
  `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, or `7000.0.0a10` alpha.
- Git for preparing test repositories and managing the checkout.
- Python 3 and Node.js for repository, Markdown, and packaging checks.

See the [lean cleanup verification](#lean-cleanup-verification-on-2026-10-19)
and the earlier records below for the checks performed on each change.
Existing version gates do not imply
that the current design has been retested across all supported targets. GitHub
CLI is not required.

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

- Target `main` for the current status and sources implementation.
- Keep all product code under `Editor/` and tests under `Tests/Editor/`.
- Avoid permanent window polling, background network discovery, and rebuilding
  unchanged views. Measure performance claims against a stated baseline.
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

## Performance Verification on 2026-10-19

The optimized `simplify-package-status` checkout was verified with Unity CLI
`1.0.0-beta.13`, Pipeline `0.8.0-exp.1`, and Unity `7000.0.0a10` on macOS.

- Fresh Unity compilation completed without errors.
- The final focused selection passed all 151 tests in the native-source and
  installed-status fixtures, including the changed compatibility seams.
- The final full Editor assembly passed all 165 tests, with no failures,
  skipped tests, or inconclusive results. It includes two Play mode cycles with
  domain reload disabled, with the original Editor settings restored.
- Real code reload was checked with Package Manager open and closed. Reopening
  retained one extension registration, one session, and one GitHub source row.
- Native sidebar recreation repaired the source without duplicate rows.
- Closing Package Manager left zero status observers, attachment retries, and
  package-owned Editor update callbacks after pending work finished.
- Two unchanged refresh requests produced no content-change notification.
- Both icons imported as 64 by 64 DXT5 textures with their original GUIDs.
- Repository validation, all 13 Python repository tests, Markdown lint, package
  dry run, and whitespace checks passed. Audited third-party artifacts remain
  unchanged.

A warmed main-thread microbenchmark compared `fe4c6b1` with the optimized source
in the same Editor. It used five batches of 20 calls after warm-up; timing and
allocation include reflection invocation by the measurement harness, but not
CLI transport or dynamic compilation. These are isolated operation costs, not
Editor startup measurements.

| Measurement | Before | After |
| --- | ---: | ---: |
| Median allocation per repeated native attachment | 142,488 bytes | 376 bytes |
| Median time per repeated native attachment | 0.325 ms | 0.00041 ms |
| Loaded dark icon, Unity-reported runtime memory | 1,174,504 bytes | 9,184 bytes |

The optimized host no longer calls attachment every second. The icon result
includes the import format changing from RGBA32 to DXT5 when downsizing made
compression possible; other platforms can use different texture formats.
Removing the Newtonsoft dependency avoids requiring it for this package, but
this workspace still retains it through Pipeline. Keeping the audited patching
libraries sets a floor on archive size; additional regression tests slightly
increase the package's own source payload.

A separate headless startup comparison used two isolated minimal projects on
Unity `7000.0.0a10`: one with `fe4c6b1` and one with the optimized source. Each
project received one warm-up followed by three alternating warm launches.
Package tests were disabled, and existing Editors were left running. All eight
launches exited successfully without compiler errors.

| Warm startup measurement | Before | After |
| --- | ---: | ---: |
| Median CLI launch-to-exit time | 4.461 s | 4.353 s |
| CLI launch-to-exit range | 4.271 to 7.195 s | 4.342 to 4.399 s |
| Median initial asset refresh | 1.696 s | 1.716 s |
| Initial asset refresh range | 1.674 to 1.771 s | 1.674 to 1.721 s |

These overlapping results do not establish a meaningful Editor startup
speedup. Wall time includes CLI and shutdown overhead, and batch mode did not
emit an interactive project-load measurement. Newtonsoft resolved and loaded
only in the baseline project; all twelve bundled third-party DLLs remained
byte-identical between variants.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux. No player build was needed
for this Editor-only change. Signed release archives and protected CI were not
run. Local results do not establish release evidence for another Unity version,
platform, or artifact.

## Edge Case Verification on 2026-10-19

The follow-up edge-case review used the existing Unity `7000.0.0a10` Editor on
macOS, Unity CLI `1.0.0-beta.13`, and Pipeline `0.8.0-exp.1`.

- Fresh Unity compilation completed without errors.
- Regression runs reproduced missing source attachment without a single selected
  package, plus unsafe negative cached results after Git index or `.gitmodules`
  changes. The corresponding tests pass after the fixes.
- All 192 tests in `MartinCalander.GitSubmoduleManager.Editor.Tests` passed in
  the final full run, with no failures, skipped tests, or inconclusive results.
  This includes the `PackageManagerCompatibility` checks.
- Coverage includes empty and multiple selections, duplicate sessions, unrelated
  sidebars, delayed construction, registered path aliases, differently named
  embedded folders, metadata changes before and after publication, failed reads,
  and linked Git worktrees with an external index.
- Two Play Mode cycles with domain reload disabled also verified native sidebar
  repair, one GitHub source row, and no remaining attachment retries. Original
  Editor settings were restored.
- Live selection moved from a submodule to a registry package, then a UPM Git
  package, then back to the submodule. Native source labels and tooltips changed
  correctly at each step.
- Six live open/rebuild/refresh/close cycles retained exactly one source row and
  one extension registration. Each close left zero sessions, status observers,
  attachment retries, and package-owned Editor update callbacks.
- A verified script reload with Package Manager closed retained no background
  callbacks. Reopening restored one session and one GitHub source row. The final
  Editor Console contained no errors.
- Repository validation, 13 Python repository tests, Markdown lint, package dry
  run, and whitespace checks passed. Audited third-party files are unchanged.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux. No player build was needed
for this Editor-only change. Startup and allocation benchmarks were not repeated
because these fixes do not add periodic work or change package dependencies.
Signed release archives and protected CI were not run. Local results do not
establish release evidence for another Unity version, platform, or artifact.

## Source Feature Verification on 2026-10-19

The source-details and Submodules-view changes were tested from the package
checkout in OpenSourceWorkspace with Unity CLI `1.0.0-beta.13`, Pipeline
`0.8.0-exp.1`, and Unity `7000.0.0a10` on macOS.

- Fresh Unity compilation completed without compiler errors or warnings.
- All 273 focused source-feature tests passed before final lifecycle verification.
- All 280 tests in `MartinCalander.GitSubmoduleManager.Editor.Tests` passed after
  the final C# changes, with no failures, skipped, or inconclusive tests. This
  includes `PackageManagerCompatibility` and two Play Mode cycles with domain
  reload disabled, with the original settings restored.
- Regression coverage includes source URL sanitization, unsupported protocols,
  relative and SSH URLs, branch/tag/SHA/default revision display, missing commit
  metadata, stale native package identities, uncertain classification, and
  both native sidebar entries surviving reconstruction.
- A live Package Manager pass verified 23 assertions: Submodules and GitHub
  membership, source and commit clipboard actions, selection changes, proper
  sidebar placement, two close/reopen cycles, and sidebar reconstruction.
- Closed Package Manager sessions left zero status observers, scheduled
  attachments, or package-owned Editor update callbacks after pending reads
  completed. The new features add no dependencies or background scans.
- Repository validation, 13 Python repository tests, Markdown lint, package
  dry run, and whitespace checks passed.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux. The six consumer projects
were not upgraded or reopened for these subsequent feature changes. No player
build was needed for this Editor-only change. Signed release archives were not
produced. Local validation is not release evidence for another commit, Unity
version, archive, or platform.

## Source Button Cleanup on 2026-10-19

Removed the extension's **Copy source URL** button while retaining selectable
source text and the other shortcuts. Using the existing OpenSourceWorkspace
Editor on Unity `7000.0.0a10`, macOS, CLI `1.0.0-beta.13`, and Pipeline
`0.8.0-exp.1`:

- Fresh Unity compilation passed without errors or warnings.
- All 11 `PackageStatusPresentationTests` and 39 `PackageSourceDetailsTests`
  passed, with no skipped or inconclusive tests.
- Live Package Manager inspection confirmed the removed button, selectable
  source text, and the remaining repository and folder actions.
- Repository validation, Markdown lint, package dry run, and whitespace checks
  passed.

The full assembly and separate compatibility run were not repeated for this
UI-only removal; reflection, lifecycle, and removal guards did not change.
Other supported Unity versions, Windows and Linux Editor execution, the six
consumer projects, player builds, and signed release artifacts were not retested
or produced. The preceding full-assembly result applies to the earlier source
feature revision, not this subsequent change.

## Submodule Filter Verification on 2026-10-19

The replacement of the Submodules sidebar page with **Submodules only** in the
native project list was tested in OpenSourceWorkspace with Unity CLI
`1.0.0-beta.13`, Pipeline `0.8.0-exp.1`, and Unity `7000.0.0a10` on macOS.

- Fresh Unity compilation completed without compiler errors.
- All 14 focused filter tests passed. They cover native membership, search and
  sorting, selection repair, shared window state, detached controls, migration
  from the retired page, rebuild failure, and narrow toolbar layout.
- All 280 tests in `MartinCalander.GitSubmoduleManager.Editor.Tests` passed after
  the final source changes, with no failures, skipped, or inconclusive tests.
  This includes `PackageManagerCompatibility` and two Play Mode cycles with
  domain reload disabled. Original Editor settings were restored.
- A live Package Manager pass verified 22 assertions, including sidebar repair,
  native search and sorting, selection changes, unchanged GitHub membership,
  and filter reset after closing and reopening. Script reload also reset the
  filter. Narrow and wide toolbar layouts were visually inspected.
- Closing the tested Package Manager window left zero sessions, attachment
  retries, and filter states. The final Editor Console had no errors or warnings.
- Repository validation, 13 Python repository tests, Markdown lint, package
  dry run, and whitespace checks passed. No dependencies, Git scans, network
  requests, or polling were added; audited third-party artifacts are unchanged.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux Editor execution. The six
consumer projects were not upgraded or reopened for this filter change.
Startup and allocation benchmarks were not repeated. No player build was
needed for this Editor-only change, and no signed release artifact was
produced. GitHub CI checks repository structure and packaging, not Unity
execution. Local validation is not release evidence for another Unity version,
archive, or platform.

## Filter Removal Verification on 2026-10-19

The custom submodule filter was removed in OpenSourceWorkspace using the
existing Unity `7000.0.0a10` Editor on macOS, CLI `1.0.0-beta.13`, and Pipeline
`0.8.0-exp.1`.

- Fresh Unity compilation completed without errors.
- All 102 `PackageManagerCompatibility` tests passed, including checks that
  ordinary project membership and search have no custom filter hooks.
- All 270 Editor tests passed after the final source changes, with no failures,
  skipped, or inconclusive tests. This includes two Play Mode cycles with domain
  reload disabled, with original settings restored.
- Seven live assertions passed. Updating with the old filter enabled restored
  the full native list of 22 packages, removed the custom control and styling,
  preserved native search, and retained the three installed GitHub packages.
- Screenshot capture returned blank frames; UI verification used the live
  native hierarchy and package-list state.
- Regression tests cover recovery from the retired Submodules sidebar page,
  preservation of other selected pages, and unreadable native page state.
- Repository validation, Markdown lint, package dry run, and whitespace checks
  passed. Package dependencies and audited third-party artifacts are unchanged.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux Editor execution; the six
consumer projects; startup and allocation benchmarks; and the unchanged Python
repository test suite. No player build or signed release artifact was produced.
GitHub CI checks repository structure and packaging, not Unity execution.
Local results do not establish release evidence for another Unity version,
archive, or platform.

## Details Cleanup Verification on 2026-10-19

The extra details panel and buttons were replaced by a Repository link in
Unity's existing links row. This local, uncommitted batch was verified in
OpenSourceWorkspace with Unity `7000.0.0a10` on macOS, CLI `1.0.0-beta.13`, and
Pipeline `0.8.0-exp.1`.

- Fresh Unity compilation completed without compiler errors.
- All 280 `PackageSourceFeatures` tests and all 104
  `PackageManagerCompatibility` tests passed.
- All 288 tests in the final Editor assembly passed, with no failures, skipped,
  or inconclusive tests. This includes two Play Mode cycles with domain reload
  disabled, with original settings restored.
- Thirty live assertions checked Repository before the native Documentation,
  Changelog, and Licenses links; native Locate; both workspace submodules and
  an ordinary UPM Git dependency; ordinary, empty, and multiple selections;
  sidebar reconstruction; and Package Manager close/reopen cleanup.
- An explicit script reload preserved the selected package and restored one
  Repository link and one GitHub source without a public details extension.
- Focused tests cover missing and mismatched installed identities, safe URLs,
  stale and detached selections, native link order, and owned-row cleanup.
- Screenshot capture returned blank frames. UI checks used the live native
  hierarchy, link ordering, and layout coordinates instead.
- Two CLI evaluations timed out while code reload settled. Retrying succeeded;
  the captured errors were from Pipeline, not package compilation or UI errors.
  The final Console had no errors and one collapsed FMOD device warning from
  the Play Mode cycles; no audio code was changed.
- Repository validation, Markdown lint, package dry run, and whitespace checks
  passed. Package dependencies and audited third-party artifacts are unchanged.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux Editor execution; the six
consumer projects; startup and allocation benchmarks; and the unchanged Python
repository test suite. No supplemental `dotnet build`, player build, signed
release artifact, or GitHub CI run was performed. Local results do not establish
release evidence for another Unity version, archive, or platform.

## Performance and Stability Verification on 2026-10-19

This local, uncommitted batch was verified in OpenSourceWorkspace with the
existing Unity `7000.0.0a10` Editor on macOS, CLI `1.0.0-beta.13`, and Pipeline
`0.8.0-exp.1`. The baseline was `cd45cbc` plus the uncommitted details cleanup
described above. The measurements below isolate the subsequent performance work.

- Fresh Unity compilation completed without compiler errors.
- All 299 `PackageSourceFeatures` tests and all 105
  `PackageManagerCompatibility` tests passed.
- All 307 tests in the final Editor assembly passed, with no failures, skipped,
  or inconclusive tests. This includes two Play Mode cycles with domain reload
  disabled, with original Editor settings restored.
- Fifteen live lifecycle assertions covered three close/reopen cycles during
  active scans, cancellation, stale-result rejection, and script reload with
  Package Manager open and closed. Closed-window reload started no status scan;
  after cancellation drained, no sessions, observers, workers, attachment
  retries, or package-owned Editor update callbacks remained.
- Thirty live UI assertions checked both workspace submodules, a UPM Git
  dependency, ordinary/empty/multiple selections, native Locate, Repository
  link ordering, GitHub membership, sidebar reconstruction, and close/reopen.
- Regression tests cover pre-cancelled reads, malformed combined Git output,
  linked worktrees and paths with spaces, rapid reopen, recycled and reparented
  labels, and repeat native hook installation.
- The final Editor Console reported zero errors and warnings and no compilation
  failure. Pipeline's retained buffer contained one five-second main-thread
  request timeout during diagnostics; subsequent probes and tests completed.
- Repository validation, all 13 Python repository tests, Markdown lint, package
  dry run, and whitespace checks passed. All twelve audited DLL hashes remain
  unchanged; no dependencies or import settings changed.

A warmed probe used seven batches in the same Editor, with 1,000 badge calls,
500 URL or membership calls, and 20 repeated installation calls per batch.
Allocations use `GC.GetAllocatedBytesForCurrentThread`; timings exclude CLI
transport and dynamic compilation. Hook-install measurements include reflection
invocation. These are local operation costs, not whole Editor loading times.

| Median allocation per call | Before | After |
| --- | ---: | ---: |
| Unchanged submodule badge | 196 bytes | 32 bytes |
| Submodule repository URL | 4,560 bytes | 3,640 bytes |
| UPM Git repository URL | 3,168 bytes | 2,136 bytes |
| Repeated completed hook installation | 36,632 bytes | 0 bytes |
| Unchanged GitHub membership control | 2,464 bytes | 2,464 bytes |

The badge probe fell from 0.65 to 0.07 microseconds per call, and repeated hook
installation from 42.59 to 0.04 microseconds. The unchanged membership control
also changed from 11.56 to 5.42 microseconds, showing timing variability between
runs. Allocation reductions are the stronger evidence for these small operations.
The initial native hook installation still runs eagerly to preserve deletion
protection; only repeated successful setup takes the fast path.

A status scan with `.gitmodules` now uses three Git processes instead of four.
A separate seven-sample interleaved probe of the same Git helper measured the
two old repository-discovery commands at 204 ms median and the combined command
at 100 ms. However, complete scan medians were 239 ms before and 245 ms after.
Those full-scan probes ran synchronously for measurement; production scans run
on a background task. They establish neither an overall scan speedup nor an
Editor startup improvement. No cold Editor restart comparison was repeated.

Production C# under `Editor/` decreased from 97,019 to 87,978 bytes, a reduction
of 9,041 bytes (9.3%). The package archive remains approximately the same size:
additional regression coverage and documentation offset the smaller product
code, and the twelve audited DLLs still account for about 4.8 MB of its contents.

Not retested: Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`,
`6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, `7000.0.0a7`,
`7000.0.0a8`, and `7000.0.0a9`; Windows and Linux Editor execution; and the six
consumer projects. No supplemental `dotnet build`, player build, signed release
artifact, or GitHub CI run was performed. No commits or pushes were made.
Local results do not establish release evidence for another commit, Unity
version, archive, or platform.

## Lean Cleanup Verification on 2026-10-19

The four cleanup changes after `3742a21` were verified with the existing Unity
`7000.0.0a10` Editor on macOS, CLI `1.0.0-beta.13`, and Pipeline `0.8.0-exp.1`.

- Fresh Unity compilation passed, followed by all 299 `PackageSourceFeatures`
  and 105 `PackageManagerCompatibility` tests.
- All 307 tests in the final Editor assembly passed, with zero failed, skipped,
  or inconclusive tests. This includes two Play Mode cycles with domain reload
  disabled; original Editor settings were restored. The final Editor Console
  reported no errors, warnings, or compilation failure.
- Thirty live Package Manager assertions passed, covering native links,
  submodule and GitHub views, ordinary/empty/multiple selections, sidebar repair,
  and close/reopen. Loaded assemblies confirmed the helper exists only in the
  test assembly, the retired predicate is absent, and classification uses a
  value type with only its two required fields.
- The removed installer matches all 15 files on the unchanged `legacy` branch.
  Retiring it removes 4,057 C# lines plus its build and release machinery.
- Repository validation, eight remaining Python tests, Markdown lint,
  actionlint `1.7.12`, package dry run, and whitespace checks passed. Sixteen
  smoke checks exercised the release workflow's archive staging and payload
  verification, including unsafe paths, duplicates, symlinks, changed contents
  and modes, and missing/empty attestations. No real signing was performed.
- All twelve audited DLLs and their metadata remain unchanged. The moved
  helper preserves its original GUID; the production assembly has no test
  assembly reference.

A warmed local probe of GitHub membership for the installed manager used seven
batches of 1,000 calls. Median allocation fell from 2,464 to 2,184 bytes per
call (280 bytes, about 11%). Production Editor C# decreased from 87,978 to
86,930 bytes. No Editor startup comparison was repeated. The installer was
already excluded from package archives, so its removal mainly reduces source
and maintenance overhead.

Not retested: the other gated Unity versions (`6000.3.22f1`, `6000.5.*f1`,
`6000.6.0f1`, `6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, and Unity 7 alphas
`a7`, `a8`, and `a9`), Windows or Linux Editor execution, and the six consumer
projects. No supplemental `dotnet build`, player build, signed release,
OpenUPM publication, or hosted CI run was performed. Local checks do not prove
release behavior for another commit, artifact, Unity version, or platform.

## Real Submodule Install and Removal on 2026-10-19

Package commit `65d1bfd2121361a4feb677170f68bec4879f9dde` was exercised in
OpenSourceWorkspace with the existing Unity `7000.0.0a10` Editor on macOS,
CLI `1.0.0-beta.13`, and Pipeline `0.8.0-exp.1`. This supplements the earlier
unit tests with real Git operations and live native Package Manager checks.

The test used the public PlayerPrefs Editor repository, its `upm` branch, and
commit `e7cc815ce6a2a213f889756cdcb8f9c166651cd4`, matching the project's
existing Git dependency. Package Manager remained open throughout both cycles.

1. Added `Packages/com.bgtools.playerprefseditor` with `git submodule add` and
   pinned the recorded revision. Normal Unity package resolution and compilation
   registered it as Embedded. The native package row showed **Submodule**,
   **Sources > GitHub** contained it, and **Repository** was the first link with
   the correct URL.
2. Removed the checkout with `git rm`. With the original manifest dependency
   present, Unity restored its Git installation. The Submodule badge cleared,
   while GitHub membership and the Repository link remained correct.
3. Reinstalled the submodule and temporarily removed its manifest dependency.
   Removing the checkout then removed the package from Unity registration,
   **In Project**, and **Sources > GitHub**. The removed selection cleared and
   no stale Repository link remained.
4. Restored the original manifest. Unity returned to the original Git package,
   with its Repository link and no Submodule badge. Manifest, lockfile, and
   `.gitmodules` matched their saved bytes; both preexisting submodule revisions
   and branches were unchanged. Temporary checkout, Git configuration, and
   module storage were removed, and Package Manager navigation was restored.

The removal safety predicate blocked the embedded submodule and allowed its
normal Git installation. The destructive native embedded-remove action was
not executed. Observations did not invoke the manager's private refresh;
normal Unity package resolution and reload drove the transitions. This verifies
end-to-end behavior, not isolated timing of the two-second metadata poller.

Each changed package state completed Unity compilation without C# errors.
Seven Pipeline requests timed out while imports or domain reload occupied the
main thread; subsequent observations succeeded after reload. The unchanged
PlayerPrefs Editor emitted Unity 7 analyzer warnings (`UAL0010`, `UAL0013`,
and `UAC0005`). No Git Submodule Manager integration errors were observed.
The final Console retained two errors from Pipeline timeouts and one compiler
warning; these were recorded rather than cleared. No production fix was needed.

Repository validation, Markdown lint, and whitespace checks passed. The earlier
307-test assembly result remains applicable to the unchanged manager source;
the assembly was not rerun for this documentation-only record. Other supported
Unity versions, Windows/Linux, the six consumer projects, player builds,
release packaging, signing, publication, and hosted CI were not rerun. Nothing
was pushed.

## Additional Edge Case Verification on 2026-10-19

Commit `bc65333409d01b433afd7428c02a61050fccd43c` was verified in OpenSourceWorkspace with the
existing Unity `7000.0.0a10` Editor on macOS, CLI `1.0.0-beta.13`, and Pipeline
`0.8.0-exp.1`. The additional coverage is committed separately as `b1ff887`;
the installed-identity fix and its regressions are in `bc65333`.

A new regression reproduced stale GitHub membership when Unity's native package
identity differed from the cached registration but its name and path still
matched. Four missing/different-identity cases failed before the fix, for both
embedded submodules and ordinary Git dependencies. Membership now requires the
same package ID as the cached registration, matching the Repository link's
existing boundary. Pending refresh still retains the coherent published snapshot.
This adds no Git commands, network requests, or background work.

Fresh Unity compilation passed. All 141 `PackageManagerPackageStatusTests`
passed after the fix, followed by all 105 `PackageManagerCompatibility` tests,
with no failures, skips, or inconclusive results. The 15 new cases include:

- Symlinked and oversized `.gitmodules` files fail closed without changing the
  target file or index; replacing oversized metadata restores classification.
- Included Git configuration cannot supply submodule registrations.
- Inherited `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, and `GIT_CONFIG_COUNT`
  cannot redirect discovery; original environment values are restored.
- A real SHA-256 index is accepted. Real stage 1/2/3 gitlink conflicts fail closed
  and recover when resolved. Neither scan changes the index.
- Native installed identity must match cached identity, including during a
  pending refresh. Matching identities remain visible.

The live metadata suite was repeated after the fix and passed 220 assertions,
including cleanup and the final native identity checks. It used real edits to
workspace `.gitmodules` and index entries, without private refresh calls,
package resolution, code reload, or compilation during the metadata transitions.
The same status-state object remained loaded throughout each run.

- GitHub HTTPS, public SSH, GitLab, relative, unknown SSH, and file URLs updated
  membership and links correctly. Dummy credentials and query/revision suffixes
  were removed from the browser link.
- Moving the selected package off GitHub cleared that selection. Missing
  registrations/gitlinks, malformed metadata, duplicate paths, a missing
  `.gitmodules`, and an index conflict removed unverified labels/links and
  recovered automatically when restored. Checkout-removal protection remained
  enabled; the destructive removal action was not executed.
- Native package rows, search, and sorting remained intact. Source cards returned
  to Unity's **Custom** label when unverified instead of retaining stale Git text.
- Closing Package Manager stopped polling and scans. Reopening read metadata
  changed while closed. Rapid edits converged on the final state. A dirty,
  detached checkout retained its submodule status and unmodified local content.
- Both embedded submodules and the normal PlayerPrefs Git dependency retained
  GitHub membership with matching native and registered IDs. There was exactly
  one GitHub source and no duplicate package rows.

The original `.gitmodules`, manifest, lockfile, Blockbench branch/revision,
checkout contents, and Package Manager navigation were restored. Temporary
index conflicts and test files were removed. Final native Console counts were
zero errors and warnings, with no compilation failure; older retained Pipeline
buffer entries were not cleared. Repository validation, Markdown lint,
whitespace checks, and the package dry run passed.

Not rerun: the full Editor assembly or Play Mode cycles for this narrow identity
check, the real install/removal cycles from the preceding record, the six
consumer projects, other gated Unity versions (`6000.3.22f1`, `6000.5.*f1`,
`6000.6.0f1`, `6000.6.2f1`, `6000.7.0b1`, `6000.7.0b2`, and Unity 7 `a7`, `a8`,
`a9`), Windows/Linux, player builds, supplemental `dotnet build`, hosted CI,
signing, release publication, or OpenUPM. The symlink fixture explicitly skips
Windows; the SHA-256 fixture skips only if Git lacks that format. Neither was
skipped on this machine. No branch or package was pushed.

## Repeated Install and Removal Verification on 2026-10-19

The real Git cycle was repeated against package commit `403d83a`, including the
installed-identity fix, in the existing Unity `7000.0.0a10` Editor on macOS.
CLI `1.0.0-beta.13`, Pipeline `0.8.0-exp.1`, and the same pinned PlayerPrefs
Editor revision from the earlier cycle were used. No product code changed.

- A real submodule installation registered as Embedded, displayed its Submodule
  badge, appeared under GitHub, and had the correct Repository link.
- Removing that checkout with its manifest dependency retained restored the
  normal Git package and cleared its Submodule badge.
- Reinstalling without a manifest entry and then removing the checkout removed
  it from registration, GitHub, and the active In Project view. No removed
  selection or stale Repository link remained.
- Restoring the original manifest restored the Git dependency. Native package
  IDs matched registration throughout the installed states. Removal protection
  blocked the submodule and allowed the normal Git installation; the destructive
  native removal action was not executed.

The final run passed 95 live-state and cleanup assertions. Badges were observed
before reselection; Repository links were checked after selecting the fixture.
Unity selected another package during reload, so selection persistence across
source changes is not claimed. Two probe assumptions were corrected: reflection
needed an explicit overload, and an inactive native page's cached visual states
were not evidence of its visible contents. Active-page checks passed.

Every changed package state completed Unity compilation without C# errors.
Three compile requests timed out during import/reload; fresh retries confirmed
the completed state. A detached job record was lost at reload, so its completion
was checked through a fresh compile request and status instead. Final Console
counts were zero errors and one existing PlayerPrefs analyzer warning. Retained
Pipeline timeout entries were not cleared. No manager integration error or
product fix was needed.

The manifest, lockfile, `.gitmodules`, and local Git configuration were restored
byte for byte. Both original submodule branches and revisions were preserved.
Temporary checkout and module storage were removed after ownership checks;
generated Project Auditor defaults and Package Manager navigation were restored.
Repository validation, Markdown lint, and whitespace checks passed.

Not rerun: automated EditMode/PlayMode suites, the six consumer projects,
Unity `6000.3.22f1`, `6000.5.*f1`, `6000.6.0f1`, `6000.6.2f1`, `6000.7.0b1`,
`6000.7.0b2`, `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, Windows/Linux, player
builds, package dry run, supplemental `dotnet build`, hosted CI, signing, release
publication, or OpenUPM. This commit only records QA of unchanged product code.
Nothing was pushed.
