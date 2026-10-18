# Git Submodule Manager

This experimental branch shows installed package status in Unity's native
Package Manager. Submodules are labelled **Submodule**, and **Sources > GitHub**
lists installed GitHub submodules and UPM Git dependencies.

## Start Here

1. Follow [Installation](installation.md) to load this branch into a test project.
2. Open **Window > Package Management > Package Manager**.
3. Select a package to inspect its installation source and submodule status.
4. Select **Sources > GitHub** to browse the project's installed GitHub packages.

The extension reads local project state and performs no GitHub discovery. Use
Unity for ordinary packages and Git for submodule installation, updates, and
removal. No GitHub CLI or account setup is required for this view.

Local validation of this branch passed on Unity `7000.0.0a10` on macOS,
including all 133 EditMode tests. The
published `0.8.1` release contains the earlier management workflow.

## Documentation

- [Installation and compatibility](installation.md)
- [User guide](user-guide.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture and safety model](architecture.md)
- [Roadmap](roadmap.md)
- [Changelog](../CHANGELOG.md)
- [Support](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SUPPORT.md)
- [Security policy](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SECURITY.md)

## License

Copyright (c) 2026 Martin Calander. Distributed under the
[MIT License](../LICENSE.md).
