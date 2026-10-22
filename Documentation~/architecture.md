# Architecture and Safety Model

## Scope

The `main` branch contains an Editor-only installed-package view and explicit
submodule package install and removal actions. It classifies
packages, labels submodules, and lists installed GitHub packages in
Unity's native Package Manager. A Repository link precedes Unity's existing
package links when a safe repository URL is available. Its scope is status
and sources, with a small Editor footprint. The native plus menu accepts a
repository link, and the native removal action removes a verified submodule
package. It does not discover remote repositories or plan dependencies. The former management implementation is preserved
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
├── PackageManagerSubmoduleActions.cs
│                               native add menu and operation lifetime
├── SubmodulePackageOperations.cs
│                               explicit Git install and removal
├── PackageManifestDocument.cs
│                               manifest validation and dependency edits
└── PackageManagerSubmoduleHarmonyPatch.cs
                                native labels and embedded-removal protection
```

## Installed State

Unity's registered-package state determines which packages are installed and
where they resolve. Local project Git metadata distinguishes an installed
submodule from an ordinary embedded, local, registry, or UPM Git package.
Classification is presentation data. It is not proof that a checkout is clean,
published, current, or safe to remove.

Successful classification returns a small immutable value containing the
repository URL and GitHub membership. Cheap metadata misses return before path
normalization. Each installed-package record validates its path once and retains
its derived Git URL information. Native presentation still checks installed
identity and path; equivalent paths are normalized and malformed paths fail
closed. Submodule registration changes remain visible in the current snapshot.

The GitHub source includes installed GitHub submodules and UPM Git dependencies.
It reuses Unity's package records, search, sorting, and selection. There is no
remote catalogue or synthetic uninstalled-package projection. The extension
never adds display records to the project's manifest or lockfile and performs
no account lookup or network scan.

The installed snapshot binds each package's source, package ID, and resolved
path. GitHub membership and the Repository link require that identity to match
Unity's current installed version, including while a new snapshot is pending.
A verified submodule's source comes from `.gitmodules`; a UPM Git
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
load. Badges and native pages share cached field-first member lookup. Status
keeps direct property-only lookup through the runtime's reflection cache. Each
access reads the current value; getter failures are not retained. A complete hook
installation is reused until teardown; incomplete seams
remain eligible for another attempt. Unchanged submodule badges retain their
state and callback, and release owned width overrides when reparented. Reusing an
attachment still checks its manager, page, and hierarchy. Refresh also verifies
that the root still owns that manager. An unreadable page identity disables the
extension's refresh handling without interrupting Unity's callback.
The compatibility gate and deletion guards remain independent.

Only observed Package Manager sessions check `.gitmodules` and the Git index,
at two-second intervals. Package registration events and explicit refresh also
request a scan; ordinary project asset changes do not. Status and installed
identity are published together. Git-only refreshes reuse the accepted installed
package dictionary, skipping repeated entry comparisons when both snapshots
share it. Package registration, reopening, and explicit refresh
invalidate its revision; retries cannot erase that invalidation. Failed or
superseded captures are never used as a new accepted snapshot. A changed metadata
stamp during a read causes a retry before publication. Content notifications update rendered labels and Repository links only when the
result actually differs. A one-time native refresh catches up an already-open
window after attachment. Later changes rebuild the GitHub list only while active;
other native lists keep their own membership, selection, and search state.
Readiness transitions do not repeat the same link refresh.
Pending or failed reads continue to block unverified embedded-folder deletion.
The deletion boundary rechecks the published metadata stamp before trusting a
negative result. Its checkout protection uses the actual direct child path
under `Packages/`, even when the folder name differs from the package ID;
submodule classification retains its stricter matching-identity requirement.
Every local scan validates the repository root, resolved Git directory, and index
location with Git. Index enumeration includes only direct `Packages/` children,
including unresolved entries; tracked files inside embedded packages do not
consume the bounded Git output allowance. The background worker can reuse index and `.gitmodules`
results independently when bounded content fingerprints and repository identity
still match a successful snapshot. Timestamp and size alone cannot authorize
reuse. Linked or oversized `.gitmodules` still fails closed. Split indexes and
indexes too large to fingerprint retain full Git reads. Zero-length metadata
does not open a fingerprint stream or authorize reuse; this avoids blocking on a
Unix pipe before Git's command timeout applies. A changed fingerprint
during a scan rejects publication and queues a fresh read.
A cold scan uses three Git commands when `.gitmodules` exists; a fully unchanged
scan uses one, and a change to either metadata file normally uses two. Closing the last
observed window invalidates and cancels an active scan. Its result cannot publish,
and each Git command checks cancellation before starting. Reopening queues a
fresh generation after that worker drains.

Submodule GitHub membership and browser URLs are derived once per distinct raw
source in the current snapshot. This cache is bounded by the registration count
and is discarded with that snapshot. Identity, resolved path, registration,
gitlink, and metadata-error checks still run before the cached result is used.
Changing a raw URL in a snapshot cannot reuse its previous source details.
Derived entries do not participate in snapshot equality or change notifications.

Classification distinguishes missing registration, missing gitlinks, package
identity and path mismatches, and metadata-read failures. These conditions
remain unverified and do not weaken deletion guards or trigger automatic repair.

Sidebar icons are imported at a maximum of 64 pixels. The package declares its
UIElements dependency, which supplies the required engine module dependencies;
it no longer declares or references Newtonsoft. The runtime-specific patching
libraries and their explicit Editor imports remain unchanged.

Ordinary package actions remain Unity-owned. A Harmony guard prevents Unity's
raw embedded-package removal from deleting a submodule folder. An explicit
submodule removal action performs independent, fresh repository and checkout
validation before modifying Git registration or package files. Cached status
is never removal authorization.

Script reload rebuilds the view from current installed state. Explicit mutations
finish their filesystem work before import and code reload are released. The
package has no dependency plan, remote discovery cache, or persisted confirmation
to resume. Existing recovery files from the earlier implementation remain
outside the new actions' ownership.

The host and status service use `OnCodeInitializing` and `OnCodeUnloading` on
Unity 6.5 and newer, with the older load and assembly-reload callbacks retained
for Unity 6.3. Initialization is idempotent. Unloading releases sessions, native
patches, event subscriptions, and pending work before the code is discarded.
Package Manager state belongs to the Editor, so its static caches and hooks are
excluded from automatic static cleanup and survive Play mode without a domain
reload. Entering or exiting Play mode does not reinstall hooks or reset installed
status. This also applies when scene reload is disabled.

This lifetime follows Unity's
[code lifecycle guidance](https://docs.unity.com/en-us/engine/7000.0/manual/programming-environment/code-reload-serialization/code-reload/code-lifecycle).
The package has no gameplay statics to reset at Play mode entry.

## Explicit Package Operations

Installation starts only from **+ > Install package as Git submodule...**. The native
input popup accepts one repository link. A separate backend validates a root
`package.json` and uses its name as a direct `Packages/` child. The default
upstream branch or an explicit branch is used. Existing installations and
occupied paths are rejected. Git processes receive argument arrays without a
shell, have bounded output and timeouts, and do not prompt interactively.
Aborted commands stop verified child processes where supported. A detached
helper that outlives Git cannot be safely identified by parentage. In that case
the operation reports unverified cleanup and retains recovery evidence; reader
resources are disposed when the helper releases its pipes, without blocking
the Editor operation.

Removal starts from the selected installed package, but independently verifies
its path, package manifest, parent gitlink, registration, Git directory, local
state, and publication before changing anything. It removes a same-name direct
dependency from `Packages/manifest.json` as well as the submodule, preventing a
shadowed dependency from reinstalling the package. Local work and ambiguous
ownership block removal. The verified checkout is moved into a unique
`Library/SubmodulePackageOperations` recovery folder before removing its gitlink
from the index. Forced removal applies only to that index entry and cannot
delete working files. This also handles staged installations and published
revision updates before a parent commit. The selected gitlink and `.gitmodules`
content are rechecked before their mutation; unrelated staged entries remain
unchanged. Checkout backups, manifest and registration backups, an operation
record, and Git object storage are retained after success as well as failure.
Hidden index flags and repositories with additional worktrees are refused.
Manifest edits use an atomic replacement with a backup of the actual previous
file, preserving concurrent edits for recovery. The operation lock coordinates
this service, but cannot prevent an independent Git client from changing files.

The UI owns the operation lifetime, runs Git work off the main thread, and
requests Unity package resolution after mutation. A missing native add-menu
contract disables that entry independently of labels, sources, and deletion
protection. No remote work is started by opening Package Manager or refreshing
status.

Toolbar lookup is cached per window with ancestry checks for replacement.
Detached menus release their active item and repair it on reattachment; unload
removes owned callbacks. Operation resource cleanup remains independent of
package resolution, including when setup or another cleanup action fails.

## Compatibility Boundary

The version gate retains exact Unity `6000.3.22f1`, Unity `6000.5.*f1` final
releases, exact `6000.6.0f1` and `6000.6.2f1`, exact `6000.7.0b1` and
`6000.7.0b2` betas. It also admits canonical `7000.0.0a*` alpha revisions with
a positive integer suffix without leading zeros. Empty, zero, overflowing,
non-ASCII, or otherwise malformed suffixes are rejected. Other Unity 7 release
channels, minor versions, and patch versions remain outside this gate, as do
Unity `6000.4` and other `6000.6` releases. The manifest's `6000.3.22f1` minimum
cannot encode this complete policy.

Passing the version gate allows the independently guarded native features to
check their required runtime contracts. It does not bypass reflection, Harmony,
installed-identity, or deletion safeguards. A missing or changed contract still
disables its affected feature. Runtime contract checks establish the expected
API shape, not behavioral compatibility with every alpha revision.

See the [verification records](../.github/CONTRIBUTING.md) for the exact tested
commits, Unity versions, platforms, and checks. Alpha eligibility, existing
version gates, and prior validation of the legacy implementation do not establish
a new release claim for the current design.

Mono retains the byte-identical Harmony 2.4.1 `net472` DLL under
`!ENABLE_CORECLR`. CoreCLR retains eleven original upstream DLLs under
`ENABLE_CORECLR`: HarmonyX 2.16.1, its pinned MonoMod dependencies, and the
complete Mono.Cecil 0.11.6 set: Cecil, Mdb, Pdb, and Rocks. Imports remain
Editor-only with explicit references. Provenance and audited hashes are pinned
in `.github/scripts/coreclr_harmony_artifacts.json` and
[Third Party Notices](../Third%20Party%20Notices.md).

## Safety Boundaries

- Keep installed-state discovery read-only. Only explicit package actions may
  change repositories or the project manifest.
- Do not treat an arbitrary `.git` marker as proof of a parent submodule.
- Keep classification bounded to the selected project's package paths.
- Preserve ordinary Unity package behavior when no submodule is involved.
- Guard reflection and Harmony hooks with exact contracts and fail closed.
- Keep runtime and player code out of the package.

## Verification

Focused EditMode tests cover installed-source classification, package paths,
GitHub source recognition, native page membership, safe Repository links,
classification boundaries, removal protection, and isolated install/removal
operations against temporary Git repositories. The
`PackageManagerCompatibility` category checks the internal contracts and Harmony
ownership needed by the native integration.

Changes to reflection require compatibility tests; lifecycle changes require the
full `MartinCalander.GitSubmoduleManager.Editor.Tests` assembly after focused
checks pass. Report each untested Unity version and platform. Repository checks
also verify metadata, local documentation links, Unity `.meta` files, dependency
artifacts, and package contents.

Assembly enumeration used by compatibility tests lives in the test assembly.
It keeps the Mono/CoreCLR distinction without adding test-only helpers to the
production Editor assembly.

The retired bootstrap installer and its tooling remain on `legacy`; current CI
and release checks cover the UPM package.
