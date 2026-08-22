# Git Submodule Manager

Git Submodule Manager is an editor-only Unity package for managing Git
submodules as embedded UPM packages under `Packages/`.

## Start Here

1. Follow [Installation](installation.md) to install Git, optionally configure
   GitHub CLI, and add the package.
2. On Unity versions with extension-page support, open **Window > Package
   Manager**, then select **Sources > GitHub**. Unity's native list, search,
   sorting, and details show installed GitHub submodules plus valid UPM packages
   discovered incrementally from authenticated personal and organization
   repositories.
3. Select a discovered package and choose **Add as Submodule** to install its
   default branch, or choose **Refresh** to rescan the project and GitHub.
4. Open **Window > Package Management > Git Submodule Manager** for the complete
   management and discovery workspace. Older Unity versions open this workspace
   as an embedded Package Manager fallback.
5. Continue with the [User Guide](user-guide.md).
6. Use [Troubleshooting](troubleshooting.md) when a CLI, credential, or
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
- represented as Git submodules;
- mounted as direct UPM packages at `Packages/com.author.package`;
- controlled explicitly from the Unity Editor.

It does not manage Git subtrees, arbitrary project folders, scoped registries,
credentials, or system package installation.

## License

Copyright (c) 2026 Martin Calander. Distributed under the
[MIT License](../LICENSE.md).
