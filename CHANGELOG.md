# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2025

### Added

- Initial release of Submodule Helper for Unity
- **In Project Tab**
  - View all installed git submodule packages
  - Display package name, branch, and path information
  - Update submodules to fetch latest changes from remote
  - Remove submodule packages with full cleanup
  - Change tracking branch for submodules
  - Warning indicators for packages missing `package.json`

- **GitHub Discovery Tab**
  - Browse repositories from authenticated GitHub account
  - Async loading with progress indicator (non-blocking UI)
  - Automatic `package.json` validation for each repository
  - Visual distinction for invalid packages (grey text)
  - Sorting options: Name, Recently Updated
  - Filtering options: All, Valid Packages Only, Public Only, Private Only
  - One-click package installation
  - Support for private repositories with warning

- **Add from URL**
  - Add any git repository as a submodule package
  - Automatic package name derivation from repository URL
  - Branch selection support
  - Package name validation (`com.author.package` format)

- **UI/UX**
  - Unity Package Manager-inspired design
  - Two-pane layout with list and details views
  - Search functionality for filtering packages
  - Last refresh timestamp display
  - Manual and automatic refresh (5-minute stale check)

- **Dependencies**
  - Git availability detection with install prompts
  - GitHub CLI availability detection with install prompts
  - Cross-platform support (macOS, Windows, Linux)

### Technical

- Async command execution using background threads
- Non-blocking GitHub API calls
- Proper PATH resolution for CLI tools across platforms
- Robust error handling and user feedback

---

## [Unreleased]

### Planned

- Package version display and comparison
- Batch update functionality
- Package dependency visualization
- Organization/team repository support
- Custom registry integration
