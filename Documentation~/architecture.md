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
├── GitSubmoduleManagerPackageManagerHost.cs
│                               Package Manager lifecycle and compatibility host
├── GitSubmoduleManagerLegacyWindow.cs
│                               compatibility redirect for the former window
├── GitSubmoduleManagerView.*   host-neutral IMGUI view and user actions
├── PackageManagerSubmoduleNativePage.cs
│                               reflected native ExtensionPage registration
├── PackageManagerSubmoduleSnapshot.cs
│                               asynchronous package-submodule classification
├── PackageManagerSubmoduleHarmonyPatch.cs
│                               Package Manager tag and source integration
├── PackageManagerGitHubDiscovery.cs
│                               lazy full-account valid-package catalogue
├── GitSubmoduleAddService.cs
│                               shared validated add and rollback transaction
├── DiscoveryCoordinator.cs    paged GitHub discovery state
├── RepositoryCoordinator.cs   lazy remote branch loading and cache
├── Models/                    package and repository data
└── Utilities/
    ├── CliCommandRunner.cs    process execution and async handles
    ├── CliInstaller.cs        consent-based native install plans and guidance
    ├── GitUtility.cs          validated Git submodule operations
    └── GitHubUtility.cs       gh authentication, parsing, and API helpers
```

## Package Manager Integration

On supported Unity 6000.5 final releases, the host registers a native **GitHub**
page through Unity's internal `ExtensionPage` contract and places its row under
**Sources**. The page
combines GitHub package submodules classified by the asynchronous installed
snapshot with valid packages published by the authenticated discovery
catalogue. Both participate in Package Manager's own list, search, sorting,
selection, and details UI. The page's **Refresh** action restarts the installed
snapshot and the remote catalogue scan.

For a valid repository that is not installed, the projection creates a
transient, non-installed placeholder in Package Manager's in-memory package
database. The placeholder exists only so Unity can render and search the
discovery result; it is never written to `Packages/manifest.json` or
`Packages/packages-lock.json`, and it is never classified as an installed
submodule. Selecting it mounts a **Repository** website link, a branch selector,
and a primary **Install** action in Unity's details and built-in action regions,
not the extension-action overflow. The selector starts on the repository's
default branch and uses `git ls-remote --heads` for additional choices. The
action passes the repository's declared package name, clone URL, and selected
branch to the shared validated add transaction.

Projection records are owned by a specific Package Manager host lifecycle.
Refresh retires the previous catalogue handles before replacing obsolete
records; window teardown releases that host's projection ownership, removing
package-created placeholders after the last host releases them; and domain
reload or Editor shutdown disposes the discovery coordinator and its process
handles. Registration, injection, action wiring, and cleanup all probe Unity's
internal contracts defensively. If a required contract is absent or an
operation cannot be proven safe, the integration fails open: Unity's package
database is not replaced or broadly cleared, installed submodules remain
available where supported, and the host-neutral management workspace remains
the fallback.

The package probes and invokes this internal contract through guarded reflection
and Harmony lifecycle hooks. Registration, row placement, and icon application
fail independently without replacing Unity's package database or selection
model. The presentation hook labels installed package submodules **Submodule**
on normal Package Manager pages and uses matching **Public** or **Private**
repository metadata on the exact GitHub extension page. It reports their
**GitHub** or **Git** source in normal Package Manager details.

The explicit **Window > Package Management > Git Submodule Manager** menu opens
the host-neutral management workspace for discovery, add, initialize, update,
retarget, and remove operations. A guarded embedded fallback remains for
contract drift and migration diagnostics, but Editors outside the Unity 6000.5
final-release line are not currently supported.

## Process Execution

Commands run through `System.Diagnostics.Process` with:

- `UseShellExecute = false`;
- redirected standard output and error;
- concurrent stream draining;
- bounded timeouts;
- `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=Never`;
- explicit executable resolution for supported editor platforms.

The runner does not interpret input through Bash, PowerShell, Command Prompt, or
another shell.

GitHub authentication uses a fixed, token-free argument list for GitHub CLI's
device flow. It pins `github.com`, asks GitHub CLI to copy the one-time code,
opens the fixed GitHub device page from Unity, keeps command output out of the
UI, and performs a separate active-account `gh api user` check before
treating discovery as authenticated. The one-click path is gated to GitHub CLI
2.79.0 or newer. Clipboard failure and older versions fall back to a compatible
command in a visible terminal so the device code is never trapped in hidden
output. The confirmation also discloses that the automated path selects HTTPS
as GitHub CLI's host-wide Git protocol.

Missing CLI tools are never installed silently. On macOS and Windows, the
management workspace shows the exact native command and requires an explicit
confirmation before starting it. Linux keeps installation in the user's
terminal so the distribution package manager can request administrator
permission normally.

## Mutation Boundary

Before a Git mutation, the utility validates:

- a secure HTTPS/SSH repository URL or explicit local repository argument;
- branch reference syntax;
- reverse-domain package name;
- a direct destination below `Packages/`.

The tool refuses mutation outside `Packages/com.author.package`. Plaintext
`http://` and `git://` remotes, embedded credentials, executable remote-helper
syntax, symlinked manifests, oversized manifests, duplicate path registrations,
and malformed or truncated structural Git output fail closed. Persisted
`.gitmodules` entries, local submodule configuration, and initialized worktree
origins are revalidated before they are used.

The native **Install** action uses the same serialized mutation service as
direct installation. It clones the branch selected in Package Manager,
then verifies the root manifest name, Git registration, destination, origin,
and branch postconditions. A failed clone or validation is rolled back only
when process termination and cleanup ownership can be proven; ambiguous state
is retained with recovery instructions instead of being deleted speculatively.

## Discovery State

The native catalogue is lazy: it starts when the GitHub source needs data. It
uses one `DiscoveryCoordinator` to walk every 50-item page for the authenticated
user, loads the organizations visible to that account, then walks every page
for each organization. Valid-package filtering is always enabled. Repository
node IDs are sent to GitHub GraphQL in bounded batches, and each confirmed root
UPM manifest is copied into an immutable catalogue snapshot as soon as its
validation batch completes. Records are deduplicated by GitHub node ID, falling
back to case-insensitive owner/name identity when no node ID is available.

Unchecked, unavailable, malformed, or non-root manifests fail closed and never
enter the catalogue. **Refresh** disposes the active coordinator and its process
handles before starting a new snapshot sequence. Missing GitHub CLI or an
authentication/API failure stops only remote discovery; the installed
submodule snapshot and management fallback remain usable.

The management workspace reuses `DiscoveryCoordinator` for its interactive
browser. Search is debounced and sent to GitHub rather than performed over a
full local account mirror. The coordinator keeps one active page request and
one newest pending request, so a stale response cannot replace a newer owner,
search, or page selection. GitHub's search result window is capped at 1,000
repositories; the UI never offers an unreachable page beyond that limit.
Rechecking or changing authentication clears account-owned state before loading
the active identity, so repositories from a previous account cannot remain
visible. In this workflow, root-manifest validation is selection-driven unless
**Valid UPM Packages** is enabled, in which case only the current page is
inspected. Branch listing also remains lazy.

## Threading

Network-heavy CLI work runs on background threads. Completion is published with
a volatile memory barrier before the editor thread consumes the result. Unity
API calls and UI mutations remain on the editor thread. Process discovery uses
platform-neutral .NET operating-system checks so background work does not query
Unity editor state.

While an asynchronous Git mutation writes below `Packages/`, automatic asset
refresh is temporarily suspended. Completion is polled independently of the
management-workspace lifecycle, validation and rollback finish first, and
auto-refresh is restored in a `finally` path before one explicit refresh. This
prevents a mid-clone domain reload from orphaning the operation.

Every mutation has a worker-owned completion outcome: succeeded, failed with a
verified rollback, or failed with repository state requiring inspection. That
safety outcome finalizes the recovery journal independently of the management
workspace's notification callback, so closing Package Manager or encountering
a GUI exception cannot misclassify an already-verified repository result.

## Failure Handling

- CLI failures retain standard error for user-visible diagnostics.
- Timeouts cancel the process tree and retain a recovery warning whenever full
  termination cannot be confirmed.
- Standard output and error are bounded; any stream that cannot be completely
  drained is marked truncated, and structural parsers discard the partial data.
- Package addition validates the cloned root and rolls back invalid packages.
- Failed clones remove safe untracked worktrees and module metadata; ambiguous
  `.gitmodules` state is preserved and reported instead of being guessed at.
- Successful `git rm` removes the tracked registration and worktree while
  retaining the submodule Git object metadata for recovery and safe re-add.
  Postconditions verify the gitlink, `.gitmodules` registration, and filesystem
  path instead of misreporting a partially completed tracked mutation.
- View detach/reattach generations prevent stale initial-load results from
  applying to a newer host lifecycle.
- Initial loading publishes Git and installed-package availability before the
  optional GitHub CLI check. A GitHub-only failure therefore never locks manual
  Git workflows, while repository mutations cancel or queue behind background
  readers without overtaking them.
- Native catalogue refresh retires discovery handles before replacement.
  Package Manager teardown unsubscribes projection callbacks and releases
  package-owned transient records; domain reload and Editor shutdown dispose
  the catalogue. Reflection or cleanup failures are contained and do not
  replace or bulk-mutate Unity's package database.
- Closing Package Manager or detaching the management workspace cancels an
  in-progress GitHub browser authentication command and retains ownership until
  its process has terminated. A session marker also blocks a second hidden
  authentication process after domain reload until Unity is restarted.

## Testing Strategy

EditMode tests cover parsing, package-name and path validation, Git reference
validation, repository URL safety, Windows path quoting, GitHub clone URL
selection, discovery paging, search, stale-request supersession, full-account
catalogue aggregation, immutable publication, organization deduplication, and
the shared add transaction's rollback outcomes.

The `PackageManagerCompatibility` EditMode category inventories the reflected
Package Manager types and exact method signatures used by supported Unity
6000.5 patch releases, then verifies Harmony ownership for every resolved hook.
The category is intentionally quick to run across patch versions and reports
all drift found within a contract group together, including the Unity version
and platform. It can also be run on other Editor generations as a migration
diagnostic, but those results do not constitute a support claim. Supported
Editors require the complete native Sources/GitHub page, projection, loading,
toolbar, and sidebar contracts.

Repository CI adds package metadata, documentation-link, Unity-meta, workflow,
and package-archive sanity checks that do not require a Unity license.
