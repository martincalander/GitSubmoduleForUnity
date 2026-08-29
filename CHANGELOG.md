# Changelog

All notable changes to Git Submodule Manager are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Added scalable package artwork that presents the Git logomark emerging from
  an open package box and now serves as the README logo.
- Added a README showcase for Cave Expedition and Big Boy Boxing using linked
  Steam capsule cards.

### Fixed

- GitHub discovery now keeps valid Unity packages from a GraphQL batch when
  unrelated repositories simply lack `package.json` or `package.json.meta`.
  Partial responses are accepted only when every reported error exactly matches
  one of those missing root files; all other command and response failures still
  fail closed.

## [2.0.0] - 2026-08-29

### Security

- Catalogue discovery now requires regular root `package.json` and
  `package.json.meta` blobs from the same commit. Direct URL installation still
  permits a valid manifest without the Unity marker, but only after a mandatory
  warning. Verified file identities are checked again after installation.
- Installs and conversions are tied to the commit inspected during preflight.
  The manager checks the resulting package identity, Git revision, and root file
  types before accepting an install or removing the original package source.
- Structural Git output, manifests, meta files, and recovery journals use
  bounded strict-UTF-8 reads. Malformed, truncated, linked, replaced, or
  oversized data is rejected instead of being treated as repository state.
- Process creation is serialized only across the redirected-pipe setup window,
  preventing concurrent commands from inheriting one another's output handles
  under Unity's Mono runtime. Normally completed commands then share a bounded
  five-second drain window, while inherited or stuck handles still fail closed.
- Submodule completion no longer trusts cached Package Manager state. Fresh
  checks bind the parent gitlink, staged and worktree `.gitmodules`, child
  origin, and `HEAD` to the current operation. The final stability check repeats
  those reads around the origin lookup; `.gitmodules` is capped at 128 KiB.
- Registry fallback for a custom dependency is tied to the complete GitHub
  catalogue revision that found no match. If catalogue coverage changes while a
  registry request is pending, resolution starts again with current data.
- Destructive operations check dirty and ignored files, exact Git index state,
  and cleanup ownership. Rollback removes only state created by the operation;
  concurrent changes and ambiguous files are kept with recovery instructions.
  Once recovery mutation starts, its closing checks cannot be cancelled.
- Submodule removal verifies the final index, worktree identity, diff, and path
  absence. Exact CRLF-to-LF normalization is supported, while linked or
  concurrently replaced files are preserved.
- Manifest edits and reload journals use identity-checked replacement and
  quarantine paths, so a late writer's bytes are recoverable rather than
  deleted. Pull-request code cannot access Unity credentials, and releases
  publish the same archive bytes tested in Unity.

The exact Git proof sequences and recovery boundaries are documented in the
[architecture and safety model](Documentation~/architecture.md).

### Added

- Preferences and Welcome now share one asynchronous setup probe. Both surfaces
  show explicit checkmarks and detected versions for Git and GitHub CLI,
  GitHub authentication status, refresh controls, and safe official
  installation or authentication guidance when setup is incomplete.
- Discovered GitHub packages can now be installed as either editable Git
  submodules or normal read-only UPM Git dependencies. Eligible installed
  packages expose **Convert to Submodule**, **Convert to Read-Only Package**,
  and **Uninstall Submodule** through Package Manager's native **Manage** menu.
- Missing-dependency preflight now lists each safely resolved source before an
  install. GitHub dependencies install leaf-first in the root package's selected
  mode, registry dependencies remain transitive, and unresolved, mismatched, or
  ambiguous requirements block the operation. Non-Unity packages prefer GitHub
  and fall back to configured registries only after complete personal and
  organization discovery proves absence; incomplete catalogue coverage fails
  closed.
- Package Manager's native **Filters** control now filters **Sources > GitHub**
  by downloaded state, repository visibility, and organization. **Status >
  Downloaded** retains packages installed as either Git submodules or read-only
  Git dependencies. Per-user Preferences provide the initial visibility,
  organization, and install-mode defaults: all repositories, all owners, and Git
  submodule respectively.
- Package Manager now presents installed submodules with a **Submodule** tag and
  a **GitHub** source using the existing themed Git icon.
- A fully native **GitHub** page under Package Manager's **Sources** section on
  Editors with extension-page support. It uses Unity's package list, search,
  sort, selection, and details UI for installed GitHub submodules and valid UPM
  packages discovered incrementally across every authenticated personal and
  organization repository page. Results are grouped as **Organization -
  _owner_**, carry **Public** or **Private** repository badges, and use Package
  Manager's native loading message and spinner while discovery is running.
- Native discovered-package details with a **Repository** website link, Git-based
  branch selector that prefers `main` and otherwise uses the repository's
  default branch, and a primary **Install** action outside Unity's **Extensions**
  overflow. Installation uses the shared validated add transaction and rolls
  back failed clones or postcondition failures when process termination and
  cleanup ownership can be proven.
- A Package Manager **+ > Install package as Git Submodule...** command that is
  available from every Package Manager page. A Git-only remote probe discovers
  branches, the default branch, and the exact root `package.json` name before
  enabling those inputs, then uses the existing trust confirmation and
  validated add transaction with safe rollback.
- Editor-only Harmony 2.4.1 integration with guarded hooks for supported Unity
  Package Manager internals.
- Guarded Package Manager removal for installed submodules, with inline
  confirmation, canonical Git cleanup, missing-worktree repair, and protection
  against Unity's raw embedded-package directory deletion.
- Public project documentation, contribution templates, security guidance, and
  UPM manual.
- Repository sanity checks and tagged-release packaging workflows.
- Open-source MIT licensing with attribution to Martin Calander.
- External Git and GitHub CLI installation guidance, plus a copyable GitHub
  authentication command that runs only in the user's visible terminal.
- Dependency-gate, installer-failure, local-repository, and branch-fetch
  regression coverage.
- A non-suppressible self-removal fallback warning for the read-only UPM
  installation, alongside guarded confirmation and transactional Git cleanup
  for installed submodules.
- Dirty-work, local-only commit, interrupted-operation, process cancellation,
  linked-worktree, and cross-platform repository integration coverage.
- A project-wide operation journal for recovery after an interrupted mutation.
- Unity-native animated loading indicators for installed packages, GitHub
  repository pages, and package-manifest validation.
- A one-time standalone Welcome window with Git, GitHub CLI, and authentication
  status, external setup guidance, a copyable authentication command, and direct
  access to **Sources > GitHub**.
- A native per-user Preferences page for routine confirmations, dependency-plan
  prompting, GitHub visibility and organization defaults, install-mode default,
  Welcome, and GitHub-source activation.

### Changed

- Package Manager's GitHub filter predicate now caches its verified reflection
  contract and reuses Unity's native filter lists, avoiding repeated assembly
  scans and list copies while rebuilding rows.
- Discovered GitHub package details now use one native **Install** dropdown with
  **Install as Git Submodule** and **Install as Read-Only Package** actions
  instead of a separate install-mode field and button.
- Package Manager dock switches and visual-tree rebuilds now retain the loaded
  GitHub catalogue and transfer projection ownership without publishing an
  empty intermediate list.
- GitHub catalogue refreshes now load two organizations concurrently while
  keeping each owner's pagination and manifest validation serialized. Refresh
  requests made during an active load are coalesced until the bounded reads
  finish, and closing the last Package Manager host lets active reads terminate
  naturally instead of force-cancelling live GitHub CLI process trees.
- Reduced editor startup, idle, and domain-reload work by activating submodule
  scans and operation polling only while their workflows are in use, retrying
  Package Manager hooks only during bounded startup/repair windows, omitting an
  unnecessary `git submodule status` process, retiring completed discovery and
  branch polling, and suppressing unchanged snapshot rebuilds. Obsolete
  internal helpers and stale legacy-window documentation were removed.
- GitHub discovery now validates a normal 50-repository page with one bounded
  GraphQL request, automatically bisects oversized responses, and avoids
  redundant Package Manager projection, list rebuilds, and package lookup work
  during loading-only progress updates. Refreshes retain the last completed
  catalogue atomically for up to 15 minutes, so installed-package
  actions remain available while replacement results load.
- Current management now lives exclusively at **Window > Package Management >
  Package Manager > Sources > GitHub** and in Package Manager's native details,
  **+**, and **Manage** controls. The setup experience is a small standalone
  Welcome window reopened from **Preferences > Git Submodule Manager**.
- Preferences now control only current native workflows: safe routine
  confirmation suppression, complete dependency-plan auto-install, GitHub
  visibility and organization defaults, default install mode, Welcome, and
  direct GitHub-source activation. Safety warnings for dirty or unverified work
  remain mandatory.
- Successful submodule installs, removals, and package-source conversions now
  hand off to Unity Package Manager's own resolve lifecycle after Git cleanup.
  The pending handoff survives assembly reloads and waits for the exact embedded,
  Git, or removed package state before allowing another package mutation.
- Dependency-aware installs now preserve their exact ordered step and phase
  across assembly reload, never reissue an attempted mutation, verify Unity's
  registered package state before advancing, and retain one terminal outcome
  until Package Manager presents it.
- Added exact Unity `6000.3.22f1` as a validated Editor target alongside Unity
  `6000.5.*f1`, and lowered the manifest eligibility minimum accordingly.
  Unity `6000.4` remains unvalidated and is not a supported target.
- Added exact Unity 6000.3 Package Manager collection, details, sidebar, list,
  filter, and multi-select removal contract alternatives while preserving the
  existing Unity 6000.5 signatures and fail-closed behavior.
- Installed repositories on **Sources > GitHub** now retain their **Public** or
  **Private** badge instead of the redundant **Submodule** badge; normal Package
  Manager pages continue to identify installed submodules explicitly.
- The top-left Git-submodule installer now uses a compact baseline layout and a
  bounded scrolling diagnostic area that expands only when status text needs it.
- Renamed the product from Git Package Manager to Git Submodule Manager,
  including the UPM package ID, editor and test assemblies, namespaces,
  serialized editor types, menu and Preferences UI, documentation, and tests.
  This is a breaking package/source identity change: existing consumers must
  replace the manifest dependency key, assembly-definition references,
  namespaces, and `GitPackageManagerWindow` references. Serialized editor types
  carry migration metadata, while legacy preferences and interrupted-operation
  state remain recoverable across the rename.
- Renamed the UPM package from `com.essentials.gitpackagemanager` to
  `com.martincalander.gitpackagemanager` and aligned its assembly, namespace,
  and Unity submodule path.
- Renamed the GitHub repository to `GitPackageManager` while keeping the UPM
  package identifier `com.martincalander.gitpackagemanager` unchanged.
- Git now gates all package operations, while GitHub CLI gates only authenticated
  repository discovery; manual additions require Git only.
- Manual additions run asynchronously, accept an empty default-branch field,
  and clean safe partial-clone artifacts after failures.
- Remote branch lists load only when opened and failed requests can be retried.
- Initialization now honors the parent repository's pinned gitlink; explicit
  updates use a clean checkout workflow with starting-commit recovery.
- Repository mutations are coordinated across window instances, run away from
  the Unity main thread where practical, and balance refresh/reload state.
- Git and GitHub CLI processes use bounded output, canonical executable paths,
  cancellation, and conservative process-tree shutdown on timeout; destructive
  recovery is skipped whenever complete termination cannot be proven.
- Discovery and branch validation serialize requests and keep only the newest
  pending selection for large repository collections.
- Initial loading publishes required Git state before optional GitHub state,
  keeps navigation responsive, and queues one exact mutation while stale
  repository readers drain.
- Package Manager refresh now rescans both installed submodules and the remote
  GitHub catalogue. Missing or unauthenticated GitHub CLI state leaves installed
  packages and Git-only direct URL installation usable.

### Removed

- Removed unused mutation-service overloads and the unreachable direct-command
  lifecycle; all active repository mutations continue through the cancellable,
  journaled task path with current assessment and fingerprint checks.
- Removed unreachable coordinator paths for the former custom GitHub search,
  debounce, and selected-repository REST manifest checks. Native Package Manager
  search now works over the catalogue validated by batched GraphQL requests.
- Removed the former package menu, management EditorWindow compatibility
  redirect, embedded manager, **In Project** tab, and interactive **Valid UPM
  Packages** filter. The native GitHub catalogue always admits only validated
  root UPM packages.

### Fixed

- Submodule removal and failed-add rollback no longer use a broad forced Git
  removal. They preserve the worktree and `.gitmodules` inode in Recovery, then
  update the exact 160000 gitlink and full staged `.gitmodules` blob together
  under one Git index lock; concurrent staged replacements and late package
  writers are preserved and fail closed. Authoritative `.gitmodules` reads are
  capped at 128 KiB before patch generation to prevent oversized allocation or
  truncated binary-patch evidence.
- Submodule removal now proves that the exact package commit is both reachable
  from a currently advertised remote branch or tag and present on the remote,
  rejects locally rewritten Git ancestry, and rechecks the complete removal
  snapshot after the bounded network proof before mutating anything.
- Project-manifest compare-and-swap now rejects linked or junction-backed
  project paths and revalidates its exact operation siblings across atomic
  replacement and recovery boundaries.
- Reload recovery now restores owned Asset Database auto-refresh suppression on
  every terminal path, verifies installed package dependency fingerprints, and
  keeps damaged or uncorrelated native Package Manager operations blocked rather
  than issuing or accepting a second mutation.
- Native Package Manager integration now activates only on the exact validated
  Unity versions, keeps conversions in **Manage**, retains discovery state when
  projection enumeration fails, requires authoritative Git branch data before
  installation, and stops snapshot polling when no host or work remains.
- Release validation now behaves consistently on Windows, includes required
  attribution in package archives, and checks out and revalidates the exact
  annotated tag without persisting repository credentials.
- Resolved native actions from Package Manager's exact active-page selection
  after package resolves or script reloads, preventing a recycled stale toolbar
  package from disabling an installed submodule's **Manage** actions.
- Reset retained-catalogue inspection throttling with each discovery lifecycle,
  so an earlier host timestamp cannot postpone expiry after refresh or teardown.
- Made the native GitHub package **Install** action and the Package Manager **+**
  installer use inline confirmation, progress, and error states so they remain
  responsive in automated GUI Editors where Unity suppresses modal dialogs.
- Blocked submodule removal when modified, untracked, conflicted, unpushed, staged,
  or otherwise ambiguous work could be lost; recovery metadata is preserved.
- Preserved staged and unstaged `.gitmodules` state by refusing ambiguous parent
  mutations and validating index locks before changes.
- Accepted valid UPM package names containing hyphens or underscores while
  requiring the exact case-sensitive `Packages/` directory.
- Rejected executable Git remote helpers, embedded URL credentials, unsafe browser
  schemes, plaintext `http://`/`git://` transports, and mismatched stale
  submodule metadata.
- Resolved module metadata through Git so remove/re-add works in linked worktrees.
- Validated bounded regular UTF-8 manifests after cloning, rejected duplicate
  submodule registrations, and discarded truncated structural Git output.
- Verified the exact package name, version, and dependency map captured during
  preflight after each dependency-aware submodule or read-only Git install.
  Mismatches trigger owned rollback or removal; incomplete cleanup reports that
  the checkout or `Packages/manifest.json` entry may remain.
- Preserved the remote-default branch for direct URL installation when no branch
  is configured, instead of silently forcing or displaying `main`.
- Kept installed navigation usable during optional GitHub failures, prevented
  stale GitHub account data after re-authentication, and clamped virtualized list
  scrolling after result changes.
- Finalized recovery journals from worker-owned safety outcomes so a closed
  EditorWindow or notification exception cannot turn a verified operation into
  a false unsafe-recovery state.
- Declared the Unity JSON serialization module used by manifest and recovery
  journal parsing so minimal Unity projects receive every required module.
- Rolled back in-memory user preferences when Unity cannot save `UserSettings`,
  and reported the failure inside Preferences instead of throwing through IMGUI.
- Counted a manually opened first-time welcome page as shown, preventing an
  unexpected second automatic presentation on the next window open.
- Kept the notification-safety regression test from writing its intentional
  callback exception to Unity's Console while preserving production reporting.

## [1.0.0] - 2026-07-12

This was a development snapshot; no `v1.0.0` tag or GitHub release was
published.

### Added

- Editor-only management for Git submodule packages below `Packages/`.
- Installed-package view with initialization state, branch, commit, path, URL,
  and root package metadata.
- Explicit initialize, update, branch-change, and remove operations.
- Direct URL installation with package-name validation and rollback.
- GitHub user and organization discovery through authenticated GitHub CLI.
- Fifty-item paging, debounced server-side search, visibility filtering, and
  name or recently-updated sorting.
- Lazy remote branch and root `package.json` validation.
- Platform-specific Git and GitHub CLI detection and install guidance.
- EditMode regression coverage for parsing, path and ref validation, URL safety,
  Windows quoting, clone URL selection, paging, and stale discovery requests.

### Changed

- Limited product scope to direct Git submodules at
  `Packages/com.author.package`; legacy subtree support was removed.
- Removed automatic initialization and other network mutations during editor
  startup.
- Removed system package-manager and downloaded-script execution from Unity.
- Updated UI colors for Unity light and dark themes and cached list-row styles.
- Updated process execution to publish completion safely across threads, drain
  output streams concurrently, disable credential prompts, and enforce
  timeouts.

### Fixed

- Used GitHub clone URLs instead of REST API endpoint URLs for discovered
  repositories.
- Prevented stale owner, page, or search responses from replacing newer results.
- Applied installed-state marking and sorting after discovery completed.
- Preserved Windows backslashes in quoted process arguments.

## [0.1.0] - 2025

### Added

- Initial experimental Submodule Helper editor window.
- Basic project submodule listing, update, remove, and branch operations.
- Early GitHub discovery and direct URL installation.

[Unreleased]: https://github.com/martincalander/GitSubmoduleManager/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/martincalander/GitSubmoduleManager/compare/5644e381f90f883aa9d12bbdca9efbf5c2b2eb05...v2.0.0
[1.0.0]: https://github.com/martincalander/GitSubmoduleManager/tree/5644e381f90f883aa9d12bbdca9efbf5c2b2eb05
[0.1.0]: https://github.com/martincalander/GitSubmoduleManager/tree/49bac435
