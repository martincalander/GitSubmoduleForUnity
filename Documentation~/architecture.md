# Architecture and Safety Model

## Scope

The `simplify-package-status` branch is an Editor-only installed-package view.
It classifies packages, labels submodules, and lists installed GitHub packages
in Unity's native Package Manager. It does not discover remote repositories,
plan dependencies, or change installed packages.

```text
Editor/
├── GitSubmoduleManagerPackageManagerHost.cs
│                               Package Manager lifecycle and details
├── PackageManagerPackageStatus.cs
│                               installed source and submodule classification
├── PackageManagerSubmoduleNativePage.cs
│                               Sources > GitHub for installed packages
└── PackageManagerSubmoduleHarmonyPatch.cs
                                native labels and embedded-removal protection
```

## Installed State

Unity's registered-package state determines which packages are installed and
where they resolve. Local project Git metadata distinguishes an installed
submodule from an ordinary embedded, local, registry, or UPM Git package.
Classification is presentation data. It is not proof that a checkout is clean,
published, current, or safe to remove.

The GitHub source includes installed GitHub submodules and UPM Git dependencies.
It reuses Unity's package records, search, sorting, and selection. There is no
remote catalogue or synthetic uninstalled-package projection. The extension
never adds display records to the project's manifest or lockfile and performs
no account lookup or network scan.

## Package Manager Integration

The host owns extension registration and updates the public package-details
extension when selection changes. Native page registration and package labels
use guarded internal contracts. Each reflected seam is checked independently;
an unavailable seam disables that feature without replacing unrelated Package
Manager pages.

The native sidebar lifecycle and public details element's panel attachment
discover the native window root, including when the details panel is absent
because no package or several packages are selected.
Each root owns one session, with a detach callback that releases its observer
and scheduled work. A single delayed scan reconnects windows already present at
code reload; there is no permanent window polling. Native construction retries
run at 100 ms intervals for at most 50 attempts, stop after success, and can
resume on a later attachment observation, sidebar rebuild, or installed-status
change. A detached row or native sidebar rebuild schedules repair on the next
UI tick. The sidebar hook notifies only the session owning that sidebar, and
source attachment fails closed if the hook could not be installed.

Native-page contracts and reflected members are cached for the current code
load. Reusing an attachment still checks its manager, page, and hierarchy.
The compatibility gate and deletion guards remain independent. Reload resets
the host's registration flag because Unity clears its public extension registry.

Only observed Package Manager sessions check `.gitmodules` and the Git index,
at two-second intervals. Package registration events and explicit refresh also
request a scan; ordinary project asset changes do not. Status and installed
identity are published together. A changed metadata stamp during a read causes
a retry before publication. Readiness notifications update details while
content notifications rebuild views only when the result actually differs.
Pending or failed reads continue to block unverified embedded-folder deletion.
The deletion boundary rechecks the published metadata stamp before trusting a
negative result. Its checkout protection uses the actual direct child path
under `Packages/`, even when the folder name differs from the package ID;
submodule classification retains its stricter matching-identity requirement.

Sidebar icons are imported at a maximum of 64 pixels. The package declares its
UIElements dependency, which supplies the required engine module dependencies;
it no longer declares or references Newtonsoft. The runtime-specific patching
libraries and their explicit Editor imports remain unchanged.

Ordinary package actions remain Unity-owned. A small Harmony guard prevents
Unity's raw embedded-package removal from deleting a submodule folder. It
performs no Git or filesystem mutation and supplies no replacement removal
workflow. Users manage submodule changes through Git.

Script reload must rebuild the view from current installed state. The package
has no installation transaction, dependency plan, pending mutation handoff,
remote discovery cache, or persisted confirmation to resume. Existing recovery
files from the earlier implementation remain outside this view's ownership.

## Compatibility Boundary

The existing gate retains exact Unity `6000.3.22f1`, Unity `6000.5.*f1` final
releases, exact `6000.6.0f1` and `6000.6.2f1`, exact `6000.7.0b1` and
`6000.7.0b2` betas, and exact `7000.0.0a7`, `7000.0.0a8`, `7000.0.0a9`, and
`7000.0.0a10` alphas. Unity `6000.4`, other `6000.6` releases, and other Unity 7
builds remain unsupported. The manifest's `6000.3.22f1` minimum cannot encode
this complete matrix.

Local validation of the simplified branch passed on Unity `7000.0.0a10` on
macOS. See the [verification record](../.github/CONTRIBUTING.md#edge-case-verification-on-2026-10-19)
for the final checks. Existing version gates and prior validation of the larger
implementation do not establish a new release claim for this branch.

Mono retains the byte-identical Harmony 2.4.1 `net472` DLL under
`!ENABLE_CORECLR`. CoreCLR retains eleven original upstream DLLs under
`ENABLE_CORECLR`: HarmonyX 2.16.1, its pinned MonoMod dependencies, and the
complete Mono.Cecil 0.11.6 set: Cecil, Mdb, Pdb, and Rocks. Imports remain
Editor-only with explicit references. Provenance and audited hashes are pinned
in `.github/scripts/coreclr_harmony_artifacts.json` and
[Third Party Notices](../Third%20Party%20Notices.md).

## Safety Boundaries

- Read installed state without changing repositories, manifests, or lockfiles.
- Do not treat an arbitrary `.git` marker as proof of a parent submodule.
- Keep classification bounded to the selected project's package paths.
- Preserve ordinary Unity package behavior when no submodule is involved.
- Guard reflection and Harmony hooks with exact contracts and fail closed.
- Keep runtime and player code out of the package.

## Verification

Focused EditMode tests cover installed-source classification, package paths,
GitHub source recognition, native page membership, and removal protection. The
`PackageManagerCompatibility` category checks the internal contracts and Harmony
ownership needed by the simplified integration.

Changes to reflection require compatibility tests; lifecycle changes require the
full `MartinCalander.GitSubmoduleManager.Editor.Tests` assembly after focused
checks pass. Report each untested Unity version and platform. Repository checks
also verify metadata, local documentation links, Unity `.meta` files, dependency
artifacts, and package contents.
