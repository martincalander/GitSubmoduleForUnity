# Git Package Manager

Git Package Manager is an editor-only Unity package for managing Git
submodules as embedded UPM packages under `Packages/`.

## Start Here

1. Follow [Installation](installation.md) to install Git, optionally configure
   GitHub CLI, and add the package.
2. Open **Window > Package Management > Git Package Manager**.
3. Continue with the [User Guide](user-guide.md).
4. Use [Troubleshooting](troubleshooting.md) when a CLI, credential, or
   submodule command fails.

## Documentation

- [Installation and compatibility](installation.md)
- [User guide](user-guide.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture and safety model](architecture.md)
- [Roadmap](roadmap.md)
- [Changelog](../CHANGELOG.md)
- [Support](https://github.com/martincalander/GitPackageManager/blob/main/.github/SUPPORT.md)
- [Security policy](https://github.com/martincalander/GitPackageManager/blob/main/.github/SECURITY.md)

## Product Boundaries

Git Package Manager intentionally manages one workflow:

- Git repositories;
- represented as Git submodules;
- mounted as direct UPM packages at `Packages/com.author.package`;
- controlled explicitly from the Unity Editor.

It does not manage Git subtrees, arbitrary project folders, scoped registries,
credentials, or system package installation.

## License

Copyright (c) 2026 Martin Calander. Distributed under the
[MIT License](../LICENSE.md).
