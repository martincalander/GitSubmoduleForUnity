# Changelog

All notable changes to Git Package Manager are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Hardened destructive operations with exact Git-root/worktree/origin checks,
  ignored-file protection, cancellable transactions, and recovery ownership.
- Hardened CI and releases so pull-request code cannot access Unity credentials
  and published archives are the exact bytes tested in Unity.

### Added

- Public project documentation, contribution templates, security guidance, and
  UPM manual.
- Repository sanity checks and tagged-release packaging workflows.
- Open-source MIT licensing with attribution to Martin Calander.
- Explicit-consent Git and GitHub CLI installation assistance on supported
  macOS and Windows setups, with safe terminal guidance elsewhere.
- Dependency-gate, installer-failure, local-repository, and branch-fetch
  regression coverage.
- A persistent self-removal warning and a second explicit confirmation before
  Git Package Manager can remove its own submodule.
- Dirty-work, local-only commit, interrupted-operation, process cancellation,
  linked-worktree, and cross-platform repository integration coverage.
- A project-wide operation journal for recovery after an interrupted mutation.
- A lazy "Valid UPM Packages" repository filter that batch-validates root
  `package.json` manifests without issuing one GitHub process per repository.
- Unity-native animated loading indicators for installed packages, GitHub
  repository pages, and package-manifest validation.
- A one-time, theme-aware welcome and setup page with persistent user settings,
  dependency status, opt-in installers, and verified GitHub browser login.
- A native per-user Preferences page for startup, refresh, discovery-filter,
  and welcome/setup options.

### Changed

- Renamed the UPM package from `com.essentials.gitpackagemanager` to
  `com.martincalander.gitpackagemanager`, including its repository, assembly,
  namespace, installation URLs, and Unity submodule path.
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

### Fixed

- Block submodule removal when modified, untracked, conflicted, unpushed, staged,
  or otherwise ambiguous work could be lost; recovery metadata is preserved.
- Preserve staged and unstaged `.gitmodules` state by refusing ambiguous parent
  mutations and validating index locks before changes.
- Accept valid UPM package names containing hyphens or underscores while
  requiring the exact case-sensitive `Packages/` directory.
- Reject executable Git remote helpers, embedded URL credentials, unsafe browser
  schemes, plaintext `http://`/`git://` transports, and mismatched stale
  submodule metadata.
- Resolve module metadata through Git so remove/re-add works in linked worktrees.
- Validate bounded regular UTF-8 manifests after cloning, reject duplicate
  submodule registrations, and discard truncated structural Git output.
- Preserve repository-default branch semantics instead of silently forcing or
  displaying `main` when no branch is configured.
- Keep installed navigation usable during optional GitHub failures, prevent
  stale GitHub account data after re-authentication, and clamp virtualized list
  scrolling after result changes.
- Finalize recovery journals from worker-owned safety outcomes so a closed
  EditorWindow or notification exception cannot turn a verified operation into
  a false unsafe-recovery state.
- Declare the Unity JSON serialization module used by manifest and recovery
  journal parsing so minimal Unity projects receive every required module.
- Roll back in-memory user preferences when Unity cannot save `UserSettings`,
  and report the failure inside Preferences instead of throwing through IMGUI.
- Count a manually opened first-time welcome page as shown, preventing an
  unexpected second automatic presentation on the next window open.

## [1.0.0] - 2026-07-12

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

- Use GitHub clone URLs instead of REST API endpoint URLs for discovered
  repositories.
- Prevent stale owner, page, or search responses from replacing newer results.
- Apply installed-state marking and sorting after discovery completes.
- Preserve Windows backslashes in quoted process arguments.

## [0.1.0] - 2025

### Added

- Initial experimental Submodule Helper editor window.
- Basic project submodule listing, update, remove, and branch operations.
- Early GitHub discovery and direct URL installation.

[Unreleased]: https://github.com/martincalander/com.martincalander.gitpackagemanager/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/martincalander/com.martincalander.gitpackagemanager/releases/tag/v1.0.0
[0.1.0]: https://github.com/martincalander/com.martincalander.gitpackagemanager/releases/tag/v0.1.0
