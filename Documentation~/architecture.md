# Architecture and Safety Model

## Package Boundary

The package contains editor-only assemblies. No runtime assembly or player code
is emitted.

```text
Editor/
├── GitSubmoduleManagerUserSettings.cs
│                               per-user, per-project preferences
├── GitSubmoduleManagerPreferencesProvider.cs
│                               native Unity Preferences integration
├── GitSubmoduleManagerWelcomeWindow.cs
│                               standalone setup/status window
├── GitSubmoduleManagerPackageManagerHost.cs
│                               Package Manager lifecycle and activation host
├── PackageManagerSubmoduleNativePage.cs
│                               reflected native Sources/GitHub registration
├── PackageManagerGitHubDiscovery.cs
│                               lazy authenticated package catalogue
├── PackageManagerGitHubPackageProjection.cs
│                               transient native package-list projection
├── PackageManagerGitHubDetails.cs
│                               branch and install-dropdown controls
├── PackageManagerGitSubmoduleInstallMenu.cs
│                               native Package Manager + menu extension
├── PackageManagerSubmoduleManageMenu.cs
│                               native conversion and removal actions
├── PackageDependencyResolutionService.cs
├── PackageDependencyInstallPreflight.cs
├── PackageDependencyInstallPrompt.cs
├── PackageDependencyInstallPipeline.cs
├── PackageDependencyInstallCoordinator.cs
│                               safe missing-dependency planning and consent
├── GitSubmoduleAddService.cs
├── GitPackageConversionService.cs
├── GitSubmoduleRemoveService.cs
│                               validated mutation transactions
├── PackageManagerSubmoduleSnapshot.cs
│                               asynchronous installed-package classification
├── PackageManagerSubmoduleHarmonyPatch.cs
├── PackageManagerGitHubNativePresentationPatch.cs
│                               guarded Package Manager presentation hooks
├── DiscoveryCoordinator.cs    paged GitHub discovery state
├── RepositoryCoordinator.cs   lazy remote branch loading and cache
├── Models/                    package and repository data
└── Utilities/
    ├── CliCommandRunner.cs    process execution and async handles
    ├── GitUtility.cs          validated Git operations
    └── GitHubUtility.cs       gh authentication, parsing, and API helpers
```

The former management EditorWindow, compatibility redirect, and host-neutral
embedded view have been removed. Current package discovery and management are
implemented on Unity's native Package Manager surface. The Welcome window is a
small independent setup/status surface and does not manage packages itself.

## Package Manager Integration

On a validated Editor target—exact Unity `6000.3.22f1` or a Unity
`6000.5.*f1` final release—the host registers a native **GitHub** page through
Unity's internal `ExtensionPage` contract and places its row under
**Sources**. Users reach it through **Window > Package Management > Package
Manager > Sources > GitHub**, or through the explicit buttons in Welcome and
Preferences.

The page combines installed GitHub packages from the asynchronous submodule
snapshot and Package Manager's installed Git-package state with catalogue-
eligible root UPM packages from authenticated discovery. Eligibility requires
a valid root `package.json` and Unity `package.json.meta`; the latter is a
classification signal rather than a provenance or trust boundary. All records
participate in Package Manager's own list, search, sorting, filtering, selection,
details tabs, action toolbar, and loading state. **Refresh** restarts
installed-state discovery and the remote scan.

One bounded GitHub GraphQL response reads both files as regular tree entries
from the same default-branch commit. Discovery rejects missing entries,
symlinks, non-blobs, binary or truncated content, oversized data, malformed
manifests, and meta files without exactly one root `fileFormatVersion: 2` and
one nonzero 32-hexadecimal root GUID. Manifest validation cache entries are
keyed by both blob object IDs so a changed marker cannot reuse stale eligibility.

For a valid repository that is not installed, the projection creates a
transient placeholder in Package Manager's in-memory database. It exists only
so Unity can render and search the discovery result. It is never written to
`Packages/manifest.json` or `Packages/packages-lock.json` and is never treated
as installed.

Selecting a discovery result mounts a **Repository** link, a branch selector,
and one primary **Install** dropdown in Unity's native details regions. The
branch selector prefers `main`, falls back to the remote default when `main` is
unavailable, and obtains both facts plus the complete bounded branch choices
from one Git `ls-remote --symref` query. Catalogue metadata is not treated as
authoritative branch state. A failed or structurally incomplete query keeps
installation disabled until the user invokes Package Manager's native refresh;
if Git cannot identify a valid default, the user must explicitly choose one of
the completely verified branches. The install dropdown offers an editable Git
submodule or a normal read-only UPM Git dependency.

The package extends Package Manager's native **+** menu with **Install package
as Git Submodule...**. Its Git-only probe reads the repository's default branch,
remote branches, root package identity, and sibling meta marker before enabling
the corresponding fields. Both files come from the same temporary clone commit.
The probe first parses exact NUL-delimited Git tree records, accepts only
`100644` or `100755` blob entries, and reads each file by its validated object
ID. A valid regular manifest with missing, invalid, or symbolic-link meta remains
eligible only in this explicit URL workflow and adds an unavoidable warning to
the repository trust confirmation; a symbolic-link manifest is invalid.

After a package resolve or script reload, Package Manager can restore its active
page selection before recycled details-toolbar fields catch up. Native actions
therefore resolve the exact single selection through the active page and package
database before presenting or executing an action. Zero, multiple, missing, or
identity-mismatched selections fail closed; the toolbar refresh argument remains
only a presentation fallback when that independently validated selection seam is
unavailable.

For an installed verified submodule, Unity's native **Manage** menu receives
**Convert to Read-Only Package** and **Uninstall Submodule**. An eligible direct
read-only Git dependency receives **Convert to Submodule**. Read-only packages
whose `package.json` is selected from a repository subdirectory are deliberately
not convertible because a package submodule must expose its manifest at the
checkout root.

Projection records are owned by a specific Package Manager host lifecycle.
Refresh requests made during an active catalogue load are coalesced until its
bounded GitHub reads finish, avoiding forced cancellation of live process trees.
Window teardown releases that host's transient projections and lets active
bounded reads terminate naturally before coordinator disposal. Domain reload
and Editor shutdown still dispose discovery coordinators and process handles
immediately as lifecycle safety requires.

## Reflection and Compatibility Boundary

The package probes Package Manager's internal contracts through guarded
reflection and Harmony lifecycle hooks. Page registration, sidebar placement,
filters, projection, details, add-menu, Manage-menu, and presentation hooks are
validated independently. Installed package submodules are labelled
**Submodule** on normal Package Manager pages. On the exact GitHub extension
page, repository visibility is shown as **Public** or **Private**. The Source
card reports **GitHub** with the themed Git icon, or **Git** for another host.

The validated Package Manager contract targets are exact Unity `6000.3.22f1`
and Unity `6000.5.*f1` final releases; Unity `6000.4` is not a supported
contract target. `package.json` declares `6000.3.22f1` only as UPM's minimum
eligibility version because the manifest cannot encode this non-contiguous
matrix. If a required internal contract cannot be verified, that extension
feature is not installed. The package does not replace, hide, or broadly clear
Unity's package database or standard Package Manager UI, and no legacy manager
fallback is injected.

## Native Filters and Defaults

The GitHub extension contributes downloaded status, repository visibility, and
organization values to Package Manager's native **Filters** control. The native
**Downloaded** status is enforced against the installed primary package version
because Unity's extension-page predicate does not evaluate that status itself.
Visibility accepts all, public, or private repositories. Organization values use
the localized presentation form **Organization - _owner_** while the saved user
preference stores only a sanitized GitHub login.

Per-user Preferences provide default visibility, organization, and install mode.
The safe defaults are all repositories, no owner restriction, and **Git
Submodule**. Visibility and organization defaults are applied only when the
native page has no existing filter selection, so a user's current Package
Manager state is not overwritten on refresh. The install-mode default initializes
the selector when a discovered repository is selected. Both confirmation types
remain active until the user explicitly opts out of the eligible prompt.

## Dependency Planning

Before a root install mutates the project, dependency preflight considers only
manifest dependencies that are not already registered directly or transitively.
The resolver combines immutable installed-package state, Unity/default and
configured-registry search results, and the current GitHub catalogue.

A registered package satisfies a requirement only when Unity reports one
complete package identity at the exact requested version. `com.unity.*`
requirements proceed directly to registry search. Every other package name gives
GitHub priority: resolution waits for a successful terminal scan of the user's
repositories and every visible organization, chooses a unique exact GitHub
match when present, and searches configured registries only after complete
catalogue coverage proves that no GitHub package exists. A discovery error,
coverage warning, unavailable manifest, incomplete owner scan, duplicate GitHub
match, or mismatched GitHub version or install identity is blocking; registry
fallback is deliberately skipped in those cases.

Each custom-package registry fallback is bound to the exact successful terminal
catalogue revision that proved absence. If discovery revision or coverage
changes while registry search is pending, its result is discarded and the
requirement is resolved again against the current catalogue.

A plan is installable only when every missing dependency has exactly one safe,
version-compatible source. Unresolved, mismatched, or ambiguous requirements
block the root install. The confirmation lists every missing requirement and
its selected source.

GitHub dependencies are installed explicitly, leaf-first, in the same mode as
the root package. Unity/default or configured-registry dependencies remain
transitive and are left to Unity Package Manager; the extension never adds them
as direct project dependencies. The per-user automatic-dependency option skips
only the confirmation for a complete unambiguous plan. It never bypasses
resolution or safety checks.

## Process Execution

Commands run through `System.Diagnostics.Process` with:

- `UseShellExecute = false`;
- redirected standard output and error;
- concurrent stream draining;
- bounded output and timeouts;
- raw-byte, BOM-independent strict UTF-8 decoding for structural output, with
  decoder flush at EOF and only a genuine UTF-8 BOM removed;
- `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=Never`;
- explicit executable resolution for Windows, macOS, and Linux Editors.

The runner never interprets repository input through Bash, PowerShell, Command
Prompt, or another shell. The standalone Welcome window performs Git and GitHub
CLI probes on a worker thread, links to official install guidance, and exposes a
fixed copyable authentication command. Authentication remains owned by GitHub
CLI; the extension never accepts a token.

## Mutation Boundary

Before a Git mutation, the utility validates:

- a secure HTTPS/SSH repository URL or explicit local repository argument;
- branch and revision syntax;
- a reverse-domain package name;
- a direct destination below `Packages/`;
- the expected repository, package manifest, gitlink, and `.gitmodules` state.

The tool refuses mutation outside `Packages/com.author.package`. Plaintext
`http://` and `git://` remotes, embedded credentials, executable remote-helper
syntax, symlinked or oversized manifests, duplicate registrations, and malformed
or truncated structural Git output fail closed.

Submodule installation validates the cloned root manifest, Git registration,
origin, destination, revision, and branch postconditions, and requires the
worker-side checkout `HEAD` to equal the exact commit whose root metadata was
inspected before success can trigger refresh or reload. Final add verification
reads the staged `.gitmodules` blob and package gitlink together, validates the
immutable registration, and binds them to the initialized child's approved
origin and exact `HEAD`. The regular, non-linked, bounded strict-UTF-8
worktree `.gitmodules` file must match that staged blob. A terminal
index/`HEAD`, origin, index/`HEAD` sequence rejects stage-only redirects and
late origin or commit swaps; the closing proof also repeats clean-state and
interrupted-operation checks so late package files cannot be imported as a
successful install. Reload reconciliation
retains that required commit evidence but deliberately avoids synchronous Git
processes on Unity's main thread. Read-only installation pins Unity Package
Manager to the same exact inspected commit, then verifies both the exact direct
Git manifest entry and Unity's reported `PackageInfo.git.hash`. For
both modes, the installed root manifest must match the exact package name,
version, and dependency-map fingerprint captured from the selected branch before
mutation. Catalogue roots and catalogue-resolved GitHub dependencies also carry
the validated meta GUID through the reload-safe install plan and require the
installed root `package.json.meta` to match it. A mismatch rolls back the newly
added submodule or removes the newly added read-only dependency only when cleanup
ownership can be proven. Failed or ambiguous cleanup retains state with an
explicit warning to inspect the package path or `Packages/manifest.json` before
retrying.

Verified submodule meta evidence also requires the checked-out commit to retain
`package.json.meta` as a regular `100644` or `100755` Git blob. This tree-mode
postcondition is independent of its worktree representation, so settings such
as `core.symlinks=false` cannot turn a Git symbolic-link entry into trusted
package intent.

Read-only-to-submodule conversion creates and verifies the destination checkout
before removing the manifest dependency. Its root manifest is read through the
regular Git blob recorded by Unity's exact resolved commit, then validated as a
matching UPM package independently of the worktree representation. Consequently
`core.symlinks=false` cannot make a symbolic-link manifest eligible, and mutable
`HEAD` is not a tree-mode authority. Submodule-to-read-only conversion first
reads the current committed revision's root `package.json` and
`package.json.meta` from their exact regular Git blobs. The manifest must be a
valid UPM manifest declaring the selected package name, and the bounded strict
UTF-8 meta marker must contain the canonical Unity header and a nonzero GUID.
Only then does conversion record the pinned dependency before removing the
verified worktree. Package Manager removal is intercepted so Unity cannot
recursively delete a verified submodule as a raw embedded directory.

Read-only dependency edits use byte-exact atomic replacement of
`Packages/manifest.json`. Randomized replacement, displaced, and recovery
siblings are never unlinked after a mutable read. Once cleanup ownership is
confirmed, the sibling is atomically renamed to another unique same-directory
recovery path and retained there, so an in-place or replacement late writer's
bytes remain recoverable at the cleanup boundary.

The confirmation preference can suppress only clean routine removal or
conversion prompts. Dirty, unpushed, changed, or unverified-state decisions are
never silently approved.

Cached local tracking refs are not publication evidence for destructive
removal. For a clean initialized worktree, removal uses bounded Git protocol
queries to require the exact commit on the registered remote and a complete
branch-or-tag advertisement whose tip contains it. Local replacement objects
and grafted ancestry are never trusted. The complete removal assessment is
captured again after the network round trip, and conversion carries the same
path-, URL-, and commit-bound proof into its target-first removal step instead
of issuing that proof twice.

The destructive path never runs a broad `git rm -f`. It first moves the exact
package worktree and `.gitmodules` inode into project-owned Recovery, then uses
one Git-generated full-object binary patch to remove the exact 160000 gitlink
and update the exact staged `.gitmodules` blob under one Git index lock. A
different staged blob or gitlink rejects the whole patch, and a late writer at
either worktree path is preserved. The desired `.gitmodules` bytes are created
only at an absent path and verified against the final staged blob. The closing
proof rechecks the exact desired index, regular worktree identity, quiet diff,
and package-path absence after test or integration hooks can run. Exact CRLF to
LF normalization is accepted only when the normalized bytes hash to the staged
blob; arbitrary filters and working-tree encodings remain blocked. Failed-add
rollback separately captures the exact add-produced gitlink and staged
`.gitmodules` identity, and proceeds only when removing exactly the target
section reproduces the pre-add baseline. Once Recovery mutation begins, its
postconditions run non-cancellably and every unsafe outcome reports the exact
preserved paths. All authoritative `.gitmodules` reads are regular-file checked
and bounded to 128 KiB so binary patch evidence always fits the process-output
safety envelope.

## Discovery State

The native catalogue starts lazily when **Sources > GitHub** needs remote data.
One bootstrap coordinator walks the authenticated user's pages and discovers
visible organizations. Up to two organization coordinators then overlap network
and GitHub CLI latency, while pages and manifest validation remain serialized
inside each owner. Repository node IDs are sent to GitHub GraphQL in bounded
batches, and each confirmed root manifest is published into an immutable
snapshot as soon as its validation batch completes. Aggregation and immutable
snapshot publication remain on Unity's main thread.

Automatic catalogue eligibility reads the root `package.json` and
`package.json.meta` tree entries from one captured default-branch commit in the
same bounded GraphQL batch. Both paths must be regular, complete text blobs;
the meta file must contain Unity's format marker and one nonzero 32-character
hexadecimal GUID. Validation results are cached only by the collision-safe pair
of manifest and meta blob object IDs, and the validated GUID is retained in the
immutable catalogue record for install-time revalidation.

Records are deduplicated by GitHub node ID, falling back to case-insensitive
owner/name identity. Unavailable, malformed, non-root, or unchecked manifests
never enter the catalogue. Missing GitHub CLI or an authentication/API failure
stops only remote discovery; installed-package snapshots and Git-only direct URL
installation remain available.

Search, sorting, visibility filtering, and organization filtering operate on the
projected records through Package Manager's native controls. Discovery retains
only the current scan generation, so stale owner, page, or refresh results cannot
replace newer catalogue state. Branch listing remains lazy.

## Threading and Reload Handoff

Network-heavy CLI work runs on background threads. Completion is published with
a memory barrier before the Editor thread consumes it. Unity API calls and UI
mutations remain on the Editor thread.

While an asynchronous Git mutation writes below `Packages/`, automatic asset
refresh is temporarily suspended. Validation and rollback finish before
auto-refresh is restored in a `finally` path. Successful install, conversion,
or removal then hands off to Unity Package Manager's resolve lifecycle. Pending
handoff state survives assembly reload and waits for the expected embedded, Git,
or removed package state before another mutation begins.

Dependency-aware installation persists its operation identity, ordered steps,
exact manifest expectations, and current phase in session state. Attempted or
waiting steps are never reissued after a reload; the coordinator resumes by
inspecting Unity's authoritative registered-package state. Its terminal success
or failure is retained across reload until a matching Package Manager details
surface or recovery dialog presents it successfully, then consumed so it is not
shown twice.

A registered submodule never advances a dependency step from the Package Manager
presentation snapshot's cached commit alone. The coordinator requests a fresh
proof bound to its runtime scope, persisted operation, exact step index,
`Packages/<package-name>` path, repository URL, and inspected commit. A worker
reads the current origin, requires one matching path/URL/branch section in both
the worktree `.gitmodules` and its immutable stage-0 blob, and repeats the parent
gitlink and initialized submodule `HEAD` checks. The final parent-index read
binds that same `.gitmodules` blob identity and exact gitlink. Each worktree
registration read is one regular, non-linked, strict-UTF-8 file of at most 128
KiB whose raw Git blob identity equals the staged blob; the terminal proof
repeats this identity check. The terminal stability sequence is index/`HEAD`,
origin, then index/`HEAD` again, preventing an origin-only change from hiding
behind stable commit evidence without reopening the commit-read seam. Pending
proof keeps the step waiting; unstable, mismatched, truncated, invalid-UTF-8,
failed, or unconfirmed process evidence fails closed. Reload, step advancement,
and intervening manager mutation activity retire the proof so another operation
or step cannot reuse it.

If a persisted primitive or coordinator record is damaged, or a native
completion retains the operation identity but loses its exact package identity,
the raw record remains recovery evidence and the session stays mutation-blocked.
The failure is presented at most once; no step is accepted or reissued. The user
must inspect the manifest, registered packages, submodule metadata, and parent
Git state before restarting the Editor to clear that session-only recovery
block.

Every operation has a worker-owned completion outcome: succeeded, failed with a
verified rollback, or failed with repository state requiring inspection. That
safety result finalizes the recovery journal independently of a Package Manager
selection change or notification exception.

Journal evidence is one bounded regular, non-linked strict-UTF-8 snapshot. On
POSIX hosts it is opened nonblocking and without following links, so a FIFO or
late link cannot stall or redirect reload recovery. Replacement and removal
recheck exact file identity; displaced journal bytes are retained under the
project recovery directory rather than deleted, and late writers fail closed.

## Failure Handling

- CLI failures retain sanitized standard error for user-visible diagnostics.
- Timeouts cancel the process tree and retain a recovery warning whenever full
  termination cannot be confirmed.
- Incomplete bounded output is marked truncated; structural parsers reject it.
- Failed additions remove only proven untracked artifacts; ambiguous
  `.gitmodules` or manifest state is preserved.
- Exact binary index compare-and-swap postconditions verify the gitlink,
  registration, staged blob, and filesystem path. Removed worktrees and the
  pre-mutation `.gitmodules` inode remain under
  `Library/GitSubmoduleManager/Recovery` for explicit recovery or cleanup.
- Host teardown releases owned projections and callbacks. Reflection or cleanup
  failure is contained and never bulk-mutates Unity's package database.
- Domain reload and Editor shutdown dispose discovery and dependency-preflight
  readers without reclassifying a verified mutation outcome.

## Testing Strategy

EditMode tests cover parsing, path and package-name validation, Git references,
repository URL safety, Windows quoting, discovery paging and cancellation,
immutable catalogue publication, organization and visibility filters, install
mode defaults, dependency source priority and fail-closed discovery coverage,
resolution and prompting, exact post-install manifest verification, reload-safe
completion, conversion eligibility, confirmation policy, and mutation rollback
outcomes.

The `PackageManagerCompatibility` category inventories the reflected Package
Manager types and exact signatures used by the validated Unity `6000.3.22f1`
and Unity `6000.5.*f1` contracts, then verifies Harmony ownership for resolved
hooks. It reports contract drift with the Unity version and platform. Runs on
any other Editor version, including Unity `6000.4`, are migration diagnostics
and do not constitute a support claim.

Repository CI also performs package metadata, documentation-link, Unity-meta,
workflow, and package-archive checks that do not require a Unity license.
