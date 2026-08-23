# Git Submodule Manager

Git Submodule Manager is an editor-only Unity package for installing and
managing Git-hosted UPM packages as editable submodules under `Packages/` or as
normal read-only Git dependencies.

## Start Here

1. Follow [Installation](installation.md) to install Git, optionally configure
   GitHub CLI, and add the package.
2. On Unity `6000.5.*f1`, open **Window > Package Management > Package Manager**,
   then select **Sources > GitHub**. Unity's native list, search,
   sorting, and details show installed GitHub submodules plus valid UPM packages
   discovered incrementally from authenticated personal and organization
   repositories.
3. Use Package Manager's native **Filters** control for visibility and
   organization. Select a discovered package, inspect its **Repository** link,
   choose a branch, then open **Install** and choose the submodule or read-only
   action. Review any missing dependency plan, then choose **Refresh** whenever
   you need to rescan.
4. Use an installed package's native **Manage** menu to convert eligible Git
   packages or safely uninstall a submodule. The Package Manager **+** menu adds
   **Install package as Git Submodule...** for direct URL installation.
5. Open **Preferences > Git Submodule Manager** to configure defaults, show the
   standalone Welcome window, or reopen **Sources > GitHub**.
6. Continue with the [User Guide](user-guide.md).
7. Use [Troubleshooting](troubleshooting.md) when a CLI, credential, or
   submodule command fails.

## Documentation

- [Installation and compatibility](installation.md)
- [User guide](user-guide.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture and safety model](architecture.md)
- [Roadmap](roadmap.md)
- [Changelog](../CHANGELOG.md)
- [Support](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SUPPORT.md)
- [Security policy](https://github.com/martincalander/GitSubmoduleManager/blob/main/.github/SECURITY.md)

## Product Boundaries

Git Submodule Manager intentionally manages one workflow:

- Git repositories;
- represented as editable Git submodules or read-only UPM Git dependencies;
- with editable submodules mounted directly at `Packages/com.author.package`;
- controlled explicitly from the Unity Editor.

It does not manage Git subtrees, arbitrary project folders, scoped registries,
credentials, or system package installation.

## License

Copyright (c) 2026 Martin Calander. Distributed under the
[MIT License](../LICENSE.md).
