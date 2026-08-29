# Third-Party Notices

Git Submodule Manager bundles the third-party library and artwork described
below. Git and GitHub CLI remain external tools supplied separately by the user.

## Bundled Software

### Harmony 2.4.1

- File: `ThirdParty/GitSubmoduleManager.Harmony.dll`
- Project: [Harmony](https://github.com/pardeike/Harmony)
- License: MIT License
- Copyright holder: Andreas Pardeike
- Role: Editor-only patches for specific Unity Package Manager presentation
  methods

#### MIT License

Copyright (c) 2017 Andreas Pardeike

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Bundled Artwork

### Git Logomark

- Files: `Documentation~/Images/GitSubmoduleManagerCover.png`,
  `Documentation~/Images/GitSubmoduleManagerIcon.png`,
  `Documentation~/Images/GitSubmoduleManagerIcon.svg`,
  `Editor/GitEditorWindowIcon.png`, and `Editor/GitEditorWindowIconLight.png`
- Original Git logomark creator: Jason Long
- Source: [Git logo downloads](https://git-scm.com/community/logos)
- License: [Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/)
- Use: the cover and SVG adapt the official mark into open-package artwork for
  repository documentation. The PNG uses the official full-color icon.
  One-color variants are recolored for Unity's dark and light skins and appear
  in the Welcome window, Package Manager Sources row, and source presentation.

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
- Role: rendering Git Submodule Manager Preferences and the standalone Welcome
  window

The Unity module is provided by the Unity Editor installation and is not
redistributed by this repository.

## Trademarks

Unity is a trademark of Unity Technologies. GitHub is a trademark of GitHub,
Inc. Git and the Git logo are either registered trademarks or trademarks of
Software Freedom Conservancy, Inc., corporate home of the Git Project. All
trademarks belong to their respective owners. This independent project is not
endorsed by those organizations.
