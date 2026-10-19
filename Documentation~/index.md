# Git Submodule Manager

Git Submodule Manager is a lightweight view of installed packages in Unity's
native Package Manager. Submodules are labelled **Submodule**, and
**Sources > GitHub** lists installed GitHub submodules and UPM Git dependencies.
A **Repository** link appears first in the selected package's native links row
when a safe repository URL is available. The current implementation is on `main`.

## Start Here

1. Follow [Installation](installation.md) to load the current implementation.
2. Open **Window > Package Management > Package Manager**.
3. Look for **Submodule** labels on installed package submodules.
4. Choose **Sources > GitHub** for installed GitHub packages.
5. Select an installed package and use **Repository** in its links row when
   available.

The extension reads local project state and performs no GitHub discovery. Use
Unity for ordinary packages and Git for submodule installation, updates, and
removal. No GitHub CLI or account setup is required for this view.

See the [verification records](../.github/CONTRIBUTING.md) for the exact commits,
Unity versions, platforms, and checks exercised locally.

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
