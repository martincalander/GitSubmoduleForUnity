# Git Submodule Manager

Git Submodule Manager is an editor-only Unity package for installing and
managing Git-hosted UPM packages as editable submodules under `Packages/` or as
normal read-only Git dependencies.

## Start Here

1. Follow [Installation](installation.md) to install Git, optionally set up
   GitHub CLI, and add the package.
2. On a supported Unity version, open **Window > Package Management > Package
   Manager**, then select **Sources > GitHub** to browse installed and discovered
   GitHub packages.
3. Search or filter the list, choose a package and branch, then use **Install**
   to add it as a submodule or read-only package.
4. Use **Remove** for one or more selected submodules and **Manage** to convert
   an eligible package. For a repository URL, choose **+ > Install package as
   Git Submodule...**.
5. Configure defaults or reopen the Welcome window under **Preferences > Git
   Submodule Manager**.
6. Continue with the [User Guide](user-guide.md), or use
   [Troubleshooting](troubleshooting.md) when an operation fails.

The manager overrides **Remove** only when the selected installed packages
include a managed submodule. Mixed removal includes every ordinary package in
the action and finishes package scanning, removal, and resolution automatically.
Ordinary-only selections, including read-only Git packages, remain Unity-native.

## Documentation

- [Installation and compatibility](installation.md)
- [User guide](user-guide.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture and safety model](architecture.md)
- [Roadmap](roadmap.md)
- [Changelog](../CHANGELOG.md)
- [Support](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SUPPORT.md)
- [Security policy](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/SECURITY.md)

## Product Boundaries

Git Submodule Manager handles root UPM Git repositories in two forms: editable
submodules mounted directly under `Packages/`, and read-only UPM Git
dependencies. It does not manage Git subtrees, arbitrary project folders,
scoped registries, credentials, or system tools.

## License

Copyright (c) 2026 Martin Calander. Distributed under the
[MIT License](../LICENSE.md).
