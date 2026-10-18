# Git Submodule Manager

Git Submodule Manager is a lightweight view of installed packages in Unity's
native Package Manager. Submodules are labelled **Submodule**, and
**Sources > GitHub** lists installed GitHub submodules and UPM Git dependencies.
The current implementation is on `main`.

## Start Here

1. Follow [Installation](installation.md) to load the current implementation.
2. Open **Window > Package Management > Package Manager**.
3. Select a package to inspect its installation source and submodule status.
4. Select **Sources > GitHub** to browse the project's installed GitHub packages.

The extension reads local project state and performs no GitHub discovery. Use
Unity for ordinary packages and Git for submodule installation, updates, and
removal. No GitHub CLI or account setup is required for this view.

Local validation passed all 192 Editor tests on Unity `7000.0.0a10` on macOS.
See the [verification record](../.github/CONTRIBUTING.md#edge-case-verification-on-2026-10-19)
for coverage and targets that have not been retested.

The former installation, conversion, and remote discovery workflows are preserved
on [`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).
Published `0.8.1` artifacts still contain that earlier implementation. Follow
the installation guide to use the current implementation from `main`.

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
