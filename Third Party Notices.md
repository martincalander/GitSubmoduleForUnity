# Third-Party Notices

Git Submodule Manager bundles the third-party libraries and artwork described
below. Git and GitHub CLI remain external tools supplied separately by the user.

## Bundled Software

### Harmony 2.4.1 and HarmonyX 2.16.1

- Projects: [Harmony](https://github.com/pardeike/Harmony) and
  [HarmonyX](https://github.com/BepInEx/HarmonyX)
- License: MIT License
- Role: Editor-only patches for specific Unity Package Manager methods

Unity's plugin define constraints select the patching implementation. The
original `ThirdParty/GitSubmoduleManager.Harmony.dll` is Harmony 2.4.1 for .NET
Framework 4.7.2 and requires `!ENABLE_CORECLR`. Its audited bytes, GUID, and
explicit assembly reference are preserved. It matches `lib/net472/0Harmony.dll`
in the official [Lib.Harmony 2.4.1 package](https://www.nuget.org/packages/Lib.Harmony/2.4.1):

```text
353daafec180bb8e7bbe4da78f2a7cdc78067392e3a4e79dc8e7af295f2371e6
```

The following unmodified upstream DLLs require `ENABLE_CORECLR`. All are
explicitly referenced Editor-only plugins and are excluded from players. Only
their filenames are changed. No dependencies are merged or source-patched.
This selection does not expand the package's validated Unity version matrix.

| Component | Upstream package | Target framework |
| --- | --- | --- |
| HarmonyX 2.16.1 | [HarmonyX 2.16.1](https://www.nuget.org/packages/HarmonyX/2.16.1) | `netstandard2.0` |
| MonoMod.RuntimeDetour 25.3.4 | [MonoMod.RuntimeDetour 25.3.4](https://www.nuget.org/packages/MonoMod.RuntimeDetour/25.3.4) | `netstandard2.1` |
| MonoMod.Core 1.3.4 | [MonoMod.Core 1.3.4](https://www.nuget.org/packages/MonoMod.Core/1.3.4) | `netstandard2.1` |
| MonoMod.Iced 1.3.4 | [MonoMod.Core 1.3.4](https://www.nuget.org/packages/MonoMod.Core/1.3.4) | `netstandard2.1` |
| MonoMod.Utils 25.0.12 | [MonoMod.Utils 25.0.12](https://www.nuget.org/packages/MonoMod.Utils/25.0.12) | `netstandard2.1` |
| MonoMod.Backports 1.1.2 | [MonoMod.Backports 1.1.2](https://www.nuget.org/packages/MonoMod.Backports/1.1.2) | `netstandard2.1` |
| MonoMod.ILHelpers 1.1.0 | [MonoMod.ILHelpers 1.1.0](https://www.nuget.org/packages/MonoMod.ILHelpers/1.1.0) | `netstandard2.1` |
| Mono.Cecil 0.11.6 | [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) | `netstandard2.0` |
| Mono.Cecil.Mdb 0.11.6 | [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) | `netstandard2.0` |
| Mono.Cecil.Pdb 0.11.6 | [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) | `netstandard2.0` |
| Mono.Cecil.Rocks 0.11.6 | [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) | `netstandard2.0` |

The .NET Standard 2.1 MonoMod builds avoid the Span backport used by their
.NET Standard 2.0 builds. The latter fails while enumerating macOS modules on
.NET 10. HarmonyX supplies a .NET Standard API that Unity's current Editor
compiler can reference, while its detour dependencies support the .NET 10
runtime.

Unity 7000.0.0a7 loads Mono.Cecil 0.11.6.0 for this configuration, including
consumers of the lower 0.11.5.0 assembly shipped in Unity's
`com.unity.nuget.mono-cecil` package. The matching Mdb and Pdb symbol readers and
Rocks extensions are included so all four Cecil assemblies use version
0.11.6.0. The older 0.11.5.0 symbol readers have incompatible interface methods
and fail type loading against Cecil 0.11.6.0 during a cold Editor start. The
package filename prefix prevents an importer filename collision; it does not
change the assembly identity.

#### CoreCLR Artifact Provenance

The DLLs are extracted from the exact official NuGet packages above. The
[artifact manifest](https://github.com/martincalander/GitSubmoduleForUnity/blob/main/.github/scripts/coreclr_harmony_artifacts.json)
records download URLs, archive SHA-256 values, source paths, and DLL SHA-256
values. Repository validation checks the packaged DLL hashes and importer
constraints.

| Packaged file | SHA-256 |
| --- | --- |
| `ThirdParty/CoreCLR/GitSubmoduleManager.Harmony.dll` | `70e7647876b6c3d874ea99b56627a95d1dce2e1dd791981a002578a2febc9bbd` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.RuntimeDetour.dll` | `c186cc37e3cc3368540d0c2857b20c4f59cd1ddfe5c58c460f69a76a3e68c42d` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Core.dll` | `6b06ad52612efd0534660f9ff4b68dcee685f5d604ff6ebe281f9041329c4376` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Iced.dll` | `42d9fc0cc12b37a61c832fca5768f62bd4d429890d34ba5c1acc30e150a99ffe` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Utils.dll` | `19f8df7d0d8ab311ad7f3a134beb82a87d4f1d26f9708b0bdd653f787dbf21a0` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.Backports.dll` | `b3fc7aac52ffe6579ae1009a285a3d892b167151b0e18b5c1066ccb88e8b44a4` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.MonoMod.ILHelpers.dll` | `a13590452f2bdf72718f43b410f43e110f273fce57fabd58ac5043a59b9be049` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.dll` | `831dca77470d85cb6ffbea3072daa7a3df5b7c9fcfd9c3f43674a9be99d4bfcf` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Mdb.dll` | `28cb367972bdc1cd43e4006306af2fd96d37f4ed4b239ee90e1dc7237a93af7f` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Pdb.dll` | `a332332633fbcb20e8d50e49b4db7bd1557721417122cf0c5f4c42f2332391d0` |
| `ThirdParty/CoreCLR/GitSubmoduleManager.Mono.Cecil.Rocks.dll` | `bf992f3dce364ebcc3200fa7832ef07e20b4e2dbc3a8a6213ce44e3d239db984` |

#### MIT License

Copyright (c) 2017 Andreas Pardeike

Copyright (c) 2020 BepInEx

The CoreCLR dependencies use the same MIT terms below:

- [MonoMod](https://github.com/MonoMod/MonoMod): Copyright (c) 2015 - 2020
  0x0ade; package metadata also credits 0x0ade and DaNike.
- [Mono.Cecil](https://github.com/jbevain/cecil): Copyright (c) 2008 - 2015
  Jb Evain; Copyright (c) 2008 - 2011 Novell, Inc.
- [Iced](https://github.com/icedland/iced), included in `MonoMod.Iced.dll`:
  Copyright (C) 2018-present iced project and contributors.

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
