# Contributing to Submodule Helper

Thank you for your interest in contributing to Submodule Helper! This document provides guidelines and information for contributors.

## Code of Conduct

Please be respectful and constructive in all interactions. We're all here to build something useful together.

## How to Contribute

### Reporting Bugs

1. Check if the bug has already been reported in the Issues section
2. If not, create a new issue with:
   - A clear, descriptive title
   - Steps to reproduce the problem
   - Expected behavior vs actual behavior
   - Unity version and OS information
   - Any relevant error messages or logs

### Suggesting Features

1. Check if the feature has already been suggested
2. Create a new issue with the "enhancement" label
3. Describe the feature and why it would be useful
4. Include any relevant mockups or examples

### Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Test thoroughly in Unity Editor
5. Commit with clear, descriptive messages
6. Push to your fork
7. Open a Pull Request

## Development Setup

### Prerequisites

- Unity 2021.3 or later
- Git
- GitHub CLI (for testing discovery features)

### Getting Started

1. Clone the repository into a Unity project's `Packages` folder:
   ```bash
   cd YourUnityProject/Packages
   git clone https://github.com/YourUsername/submodulehelper.git com.martincalander.submodulehelper
   ```

2. Open the project in Unity

3. Access the tool via **Window > Package Management > Git Submodules**

## Code Style

- Follow C# naming conventions
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Keep methods focused and concise
- Use `internal` for non-public APIs within the package

## Architecture Overview

```
Editor/
├── SubmoduleWindow.cs      # Main EditorWindow UI
├── MenuPath.cs             # Menu item definitions
└── Utilities/
    ├── CliCommandRunner.cs # Async CLI execution
    ├── CliInstaller.cs     # Dependency installation
    ├── GitUtility.cs       # Git submodule operations
    └── GitHubUtility.cs    # GitHub API interactions
```

### Key Components

- **SubmoduleWindow**: Main UI, handles all user interactions
- **CliCommandRunner**: Executes CLI commands with async support
- **GitUtility**: Wraps git submodule commands
- **GitHubUtility**: Wraps GitHub CLI commands

## Testing

Currently, testing is manual. When submitting changes:

1. Test the "In Project" tab with existing submodules
2. Test the "GitHub" tab with your authenticated account
3. Test adding packages via URL and from GitHub
4. Test on your target platform (macOS/Windows/Linux)

## Questions?

Feel free to open an issue for any questions about contributing.

Thank you for helping improve Submodule Helper!
