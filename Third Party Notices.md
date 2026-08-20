# Third-Party Notices

Git Submodule Manager does not bundle third-party source code, binaries, Git, or
GitHub CLI. It integrates with software supplied separately by the user or Unity
Editor. The package includes the Git logomark described below.

## Bundled Artwork

### Git Logomark

- Files: `GitSubmoduleManagerIcon.png`, `Editor/GitEditorWindowIcon.png`, and
  `Editor/GitEditorWindowIconLight.png`
- Creator: Jason Long
- Source: [Git logo downloads](https://git-scm.com/community/logos)
- License: [Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/)
- Use: the official full-color Git icon for package artwork and one-color
  variants recolored to Unity's built-in dark- and light-skin icon values for
  the Editor window tab

## External Tools

### Git

- Website: [git-scm.com](https://git-scm.com/)
- License: GNU General Public License version 2
- Role: required external executable for submodule and remote operations

### GitHub CLI

- Website: [cli.github.com](https://cli.github.com/)
- License: MIT License
- Role: optional external executable for authenticated GitHub discovery and API
  requests

## Unity Module

### Unity IMGUI

- Package: `com.unity.modules.imgui`
- License: Unity Companion License
- Role: editor window rendering

The Unity module is provided by the Unity Editor installation and is not
redistributed by this repository.

## Trademarks

Unity is a trademark of Unity Technologies. GitHub is a trademark of GitHub,
Inc. Git and the Git logo are either registered trademarks or trademarks of
Software Freedom Conservancy, Inc., corporate home of the Git Project. All
trademarks belong to their respective owners. This independent project is not
endorsed by those organizations.
