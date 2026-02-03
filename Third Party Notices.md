# Third Party Notices

This package includes or depends on the following third-party software:

## Runtime Dependencies

### Git

- **Website**: https://git-scm.com/
- **License**: GNU General Public License v2
- **Usage**: Required for all submodule operations (add, remove, update, branch management)

### GitHub CLI (gh)

- **Website**: https://cli.github.com/
- **License**: MIT License
- **Usage**: Optional. Used for GitHub repository discovery and package.json validation via GitHub API

## Unity Dependencies

### Unity IMGUI Module

- **Package**: com.unity.modules.imgui
- **License**: Unity Companion License
- **Usage**: Required for Editor UI rendering

## Notes

- Git and GitHub CLI are external tools that must be installed separately by the user
- This package does not bundle or redistribute these tools
- This package only provides a Unity Editor interface to interact with these tools via command-line
- No third-party code is directly included in this package

## Acknowledgments

This tool was inspired by Unity's Package Manager UI design and aims to provide a familiar experience for Unity developers managing git-based packages.
