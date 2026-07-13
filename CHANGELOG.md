# Changelog

All notable changes to Git Package Manager are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Public project documentation, contribution templates, security guidance, and
  UPM manual.
- Repository sanity checks and tagged-release packaging workflows.
- Open-source MIT licensing with attribution to Martin Calander.
- Explicit-consent Git and GitHub CLI installation assistance on supported
  macOS and Windows setups, with safe terminal guidance elsewhere.
- Dependency-gate, installer-failure, local-repository, and branch-fetch
  regression coverage.

### Changed

- Renamed the UPM package from `com.essentials.gitpackagemanager` to
  `com.martincalander.gitpackagemanager`, including its repository, assembly,
  namespace, installation URLs, and Unity submodule path.
- Git now gates all package operations, while GitHub CLI gates only authenticated
  repository discovery; manual additions require Git only.
- Manual additions run asynchronously, accept an empty default-branch field,
  and clean safe partial-clone artifacts after failures.
- Remote branch lists load only when opened and failed requests can be retried.

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
