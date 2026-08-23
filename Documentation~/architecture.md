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

On supported Unity `6000.5.*f1` releases, the host registers a native **GitHub**
page through Unity's internal `ExtensionPage` contract and places its row under
**Sources**. Users reach it through **Window > Package Management > Package
Manager > Sources > GitHub**, or through the explicit buttons in Welcome and
Preferences.

The page combines installed GitHub packages from the asynchronous submodule
snapshot and Package Manager's installed Git-package state with valid root UPM
packages from the authenticated discovery catalogue. All records participate in
Package Manager's own list, search, sorting, filtering, selection, details tabs,
action toolbar, and loading state. **Refresh** restarts installed-state discovery
and the remote scan.

For a valid repository that is not installed, the projection creates a
transient placeholder in Package Manager's in-memory database. It exists only
so Unity can render and search the discovery result. It is never written to
`Packages/manifest.json` or `Packages/packages-lock.json` and is never treated
as installed.

Selecting a discovery result mounts a **Repository** link, a branch selector,
and one primary **Install** dropdown in Unity's native details regions. The
branch selector prefers `main`, falls back to the remote default when `main` is
unavailable, and uses `git ls-remote --heads` for additional choices. The
install dropdown offers an editable Git submodule or a normal read-only UPM Git
dependency.

The package extends Package Manager's native **+** menu with **Install package
as Git Submodule...**. Its Git-only probe reads the repository's default branch,
remote branches, and root package identity before enabling the corresponding
fields.

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

The supported contract line is Unity `6000.5.*f1`, with `6000.5.0f1` declared as
the minimum. If a required internal contract cannot be verified, that extension
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
origin, destination, revision, and branch postconditions. Read-only installation
uses Unity Package Manager and verifies an exact direct Git manifest entry. For
both modes, the installed root manifest must match the exact package name,
version, and dependency-map fingerprint captured from the selected branch before
mutation. A mismatch rolls back the newly added submodule or removes the newly
added read-only dependency only when cleanup ownership can be proven. Failed or
ambiguous cleanup retains state with an explicit warning to inspect the package
path or `Packages/manifest.json` before retrying.

Read-only-to-submodule conversion creates and verifies the destination checkout
before removing the manifest dependency. Submodule-to-read-only conversion
records the dependency before removing the verified worktree and pins the
current committed revision. Package Manager removal is intercepted so Unity
cannot recursively delete a verified submodule as a raw embedded directory.

The confirmation preference can suppress only clean routine removal or
conversion prompts. Dirty, unpushed, changed, or unverified-state decisions are
never silently approved.

## Discovery State

The native catalogue starts lazily when **Sources > GitHub** needs remote data.
One bootstrap coordinator walks the authenticated user's pages and discovers
visible organizations. Up to two organization coordinators then overlap network
and GitHub CLI latency, while pages and manifest validation remain serialized
inside each owner. Repository node IDs are sent to GitHub GraphQL in bounded
batches, and each confirmed root manifest is published into an immutable
snapshot as soon as its validation batch completes. Aggregation and immutable
snapshot publication remain on Unity's main thread.

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

Every operation has a worker-owned completion outcome: succeeded, failed with a
verified rollback, or failed with repository state requiring inspection. That
safety result finalizes the recovery journal independently of a Package Manager
selection change or notification exception.

## Failure Handling

- CLI failures retain sanitized standard error for user-visible diagnostics.
- Timeouts cancel the process tree and retain a recovery warning whenever full
  termination cannot be confirmed.
- Incomplete bounded output is marked truncated; structural parsers reject it.
- Failed additions remove only proven untracked artifacts; ambiguous
  `.gitmodules` or manifest state is preserved.
- Canonical `git rm` postconditions verify the gitlink, registration, index, and
  filesystem path while retaining Git object metadata where possible.
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
Manager types and exact signatures used by supported Unity 6000.5 patches, then
verifies Harmony ownership for resolved hooks. It reports contract drift with
the Unity version and platform. Runs on other Editor generations are migration
diagnostics and do not constitute a support claim.

Repository CI also performs package metadata, documentation-link, Unity-meta,
workflow, and package-archive checks that do not require a Unity license.
