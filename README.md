# Submodule Helper for Unity

A Unity Editor tool that provides a visual interface for managing Git submodules as Unity packages. Designed to mirror the Unity Package Manager experience, making it easy to discover, install, update, and remove packages from your GitHub repositories.

## Features

- **Package Manager-style UI** - Familiar interface matching Unity's Package Manager design
- **In Project View** - See all installed submodule packages at a glance
- **GitHub Discovery** - Browse your GitHub repositories and install them as packages
- **Async Loading** - Non-blocking repository fetching with progress indicators
- **Package Validation** - Automatically checks for `package.json` in repositories
- **Sorting & Filtering** - Filter by valid packages, public/private repos, and sort by name or recent activity
- **Branch Management** - Switch branches and update submodules with ease
- **Private Repo Support** - Works with private repositories (collaborators need access)
- **Fresh Clone Recovery** - Automatically initializes missing submodules on editor load

## Requirements

- **Unity 2021.3** or later
- **Git** - Must be installed and accessible from command line
- **GitHub CLI** (optional) - Required for the GitHub discovery feature
  - Install: `brew install gh` (macOS) or `winget install GitHub.cli` (Windows)
  - Authenticate: `gh auth login`

## Installation

### Via Git Submodule (Recommended)

```bash
cd YourUnityProject
git submodule add https://github.com/YourUsername/submodulehelper.git Packages/com.martincalander.submodulehelper
```

### Via Git URL in Package Manager

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter: `https://github.com/YourUsername/submodulehelper.git`

## Usage

### Opening the Window

Navigate to **Window > Package Management > Git Submodules** in the Unity menu.

### In Project Tab

View all currently installed git submodule packages:
- See package name, branch, and path
- Update packages to fetch latest changes
- Remove packages when no longer needed
- Change tracking branch

### GitHub Tab

Discover and install packages from your GitHub repositories:
- Lists all repositories from your authenticated GitHub account
- Grey items indicate repositories without `package.json` (not valid Unity packages)
- Filter to show only valid packages, public repos, or private repos
- Sort by name or recently updated
- One-click installation as a submodule package

### Adding Packages

**From GitHub Tab:**
1. Select a repository from the list
2. Adjust the package name and branch if needed
3. Click **Add Package**

**From Git URL:**
1. Click the **+** button in the toolbar
2. Select **Add package from git URL...**
3. Enter the repository URL, branch, and package name
4. Click **Add**

## How It Works

This tool uses Git submodules to manage Unity packages. When you add a package:

1. The repository is added as a submodule under `Packages/{package-name}`
2. The `package.json` is validated to ensure it's a valid Unity package
3. Unity automatically recognizes and imports the package

Benefits of using submodules:
- **Version Control** - Track exact commits across your team
- **Easy Updates** - Pull latest changes or switch branches
- **No Registry Required** - Works with any Git repository
- **Private Repos** - Full support for private repositories

## Troubleshooting

### Git not found
The tool will prompt you to install Git if it's not detected. On macOS, you can install via Homebrew:
```bash
brew install git
```

### GitHub CLI not authenticated
Run the following command and follow the prompts:
```bash
gh auth login
```

### Repository doesn't appear as valid package
Ensure your repository has a valid `package.json` in the root directory with the required Unity package fields.

### Package folders are empty on a fresh clone
Submodule Helper now auto-runs `git submodule update --init --recursive` when it detects uninitialized submodules.

If your environment blocks that command (for example no network or missing access), run it manually in your project root:
```bash
git submodule update --init --recursive
```

## License

Created by Martin Calander. See [LICENSE.md](LICENSE.md) for details.

## Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.
