# Architecture and Safety Model

## Scope

The `main` branch contains an Editor-only installed-package view. It classifies
packages, labels submodules, and lists installed GitHub packages in
Unity's native Package Manager. A Repository link precedes Unity's existing
package links when a safe repository URL is available. Its scope is status
and sources, with a small Editor footprint. It does not discover remote repositories, plan dependencies, or
change installed packages. The former management implementation is preserved
on [`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).

```text
Editor/
├── GitSubmoduleManagerPackageManagerHost.cs
│                               Package Manager lifecycle
├── PackageManagerPackageStatus.cs
│                               installed source and submodule classification
├── PackageSourceDetails.cs
│                               source URL validation and sanitization
├── PackageRepositoryLink.cs
│                               Repository link in Unity's native links row
├── PackageManagerSubmoduleNativePage.cs
│                               Sources > GitHub
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

The installed snapshot binds each package's source, package ID, and resolved
path. The Repository link requires that identity to match the current
selection. A verified submodule's source comes from `.gitmodules`; a UPM Git
dependency's source comes from Unity's registered package metadata. Link
resolution reuses existing reads without new Git commands, checkout `HEAD`
reads, remote requests, or polling.

Repository links allow HTTP and HTTPS URLs and map recognized public SSH hosts
to HTTPS. They omit credentials, unrelated query parameters, package paths, and
revision suffixes. Relative URLs, file URLs, and unknown SSH hosts have no
guessed browser address. Resolution builds only the browser URL; the removed
details panel has no retained revision, commit, or description model.

## Package Manager Integration

The host owns Package Manager sessions. Native page registration, package
labels, and the Repository link use guarded internal contracts. Each reflected
seam is checked independently;
an unavailable seam disables that feature without replacing unrelated Package
Manager pages. GitHub remains under Sources, and its membership predicate reads
cached installed state. The native project package list keeps Unity's existing
membership, search, and sorting. Submodule labels use the same cached
classification across Git hosts.

A saved selection of the retired Submodules page redirects to Unity's All
Packages page during attachment. This recovery is guarded independently of
GitHub source registration and leaves other selected pages unchanged.

`PackageRepositoryLink` hooks the native `PackageDetailsLinks.Refresh` method
to insert Repository first in Unity's existing Documentation, Changelog, and
Licenses row. It is available only for a matching installed selection with a
safe repository URL. Unity's existing links and package information remain in
place. The package does not register a public details extension or add a hidden
anchor, extra panel, or panel padding.

The native sidebar hook and links refresh discover the Package Manager window
root. The sidebar hook also works when no package or several packages are
selected and the details panel is absent. Each root owns one session, with a
detach callback that releases its observer and scheduled work. One delayed
catch-up scan reconnects windows and their existing links rows after code
reload; there is no permanent window polling. Native construction retries
run at 100 ms intervals for at most 50 attempts, stop after success, and can
resume on a later attachment observation, sidebar rebuild, or installed-status
change. A detached row or native sidebar rebuild schedules repair on the next
UI tick. The sidebar hook notifies only the session owning that sidebar, and
source attachment fails closed if the hook could not be installed.

Native-page contracts and reflected members are cached for the current code
load. Reusing an attachment still checks its manager, page, and hierarchy.
The compatibility gate and deletion guards remain independent.

Only observed Package Manager sessions check `.gitmodules` and the Git index,
at two-second intervals. Package registration events and explicit refresh also
request a scan; ordinary project asset changes do not. Status and installed
identity are published together. A changed metadata stamp during a read causes
a retry before publication. Readiness notifications update the Repository link while
content notifications rebuild views only when the result actually differs.
Pending or failed reads continue to block unverified embedded-folder deletion.
The deletion boundary rechecks the published metadata stamp before trusting a
negative result. Its checkout protection uses the actual direct child path
under `Packages/`, even when the folder name differs from the package ID;
submodule classification retains its stricter matching-identity requirement.
A local scan uses one Git command for repository root/index discovery, one for
the package index, and one for `.gitmodules` when it exists. Closing the last
observed window invalidates and cancels an active scan. Its result cannot publish,
and each Git command checks cancellation before starting. Reopening queues a
fresh generation after that worker drains.

Classification distinguishes missing registration, missing gitlinks, package
identity and path mismatches, and metadata-read failures. These conditions
remain unverified and do not weaken deletion guards or trigger automatic repair.

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

See the [verification records](../.github/CONTRIBUTING.md) for the exact tested
commits, Unity versions, platforms, and checks. Existing version gates and prior
validation of the legacy implementation do not establish a new release claim for the current design.

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
GitHub source recognition, native page membership, safe Repository links,
classification boundaries, and removal protection. The
`PackageManagerCompatibility` category checks the internal contracts and Harmony
ownership needed by the native integration.

Changes to reflection require compatibility tests; lifecycle changes require the
full `MartinCalander.GitSubmoduleManager.Editor.Tests` assembly after focused
checks pass. Report each untested Unity version and platform. Repository checks
also verify metadata, local documentation links, Unity `.meta` files, dependency
artifacts, and package contents.
