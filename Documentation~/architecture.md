# Architecture and Safety Model

## Package Boundary

The package contains editor-only assemblies. No runtime assembly or player code
is emitted.

```text
Editor/
├── GitPackageManagerUserSettings.cs
│                               per-user, per-project preferences
├── GitPackageManagerPreferencesProvider.cs
│                               native Unity Preferences integration
├── PackageManagerWindow.*      IMGUI rendering and user actions
├── DiscoveryCoordinator.cs    paged GitHub discovery state
├── RepositoryCoordinator.cs   lazy remote branch loading and cache
├── Models/                    package and repository data
└── Utilities/
    ├── CliCommandRunner.cs    process execution and async handles
    ├── CliInstaller.cs        consent-based native install plans and guidance
    ├── GitUtility.cs          validated Git submodule operations
    └── GitHubUtility.cs       gh authentication, parsing, and API helpers
```

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
window shows the exact native command and requires an explicit confirmation
before starting it. Linux keeps installation in the user's terminal so the
distribution package manager can request administrator permission normally.

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

## Discovery State

GitHub discovery uses `gh api` with 50-item pages. Search is debounced and sent
to GitHub rather than performed over a full local account mirror. The
coordinator keeps one active page request and one newest pending request, so a
stale response cannot replace a newer owner, search, or page selection.
GitHub's search result window is capped at 1,000 repositories; the UI never
offers an unreachable page beyond that limit. Rechecking or changing GitHub
authentication clears account-owned discovery state before loading the active
identity, so repositories from a previous account cannot remain visible.

Remote `package.json` checks and branch listing are selection-driven and lazy.
When **Valid UPM Packages** is enabled, only the current page is inspected.
Repository node IDs are sent to GitHub GraphQL in bounded batches, manifest
results are cached by Git object ID, and incomplete or malformed responses fail
closed without exposing unchecked repositories as valid packages.

## Threading

Network-heavy CLI work runs on background threads. Completion is published with
a volatile memory barrier before the editor thread consumes the result. Unity
API calls and UI mutations remain on the editor thread. Process discovery uses
platform-neutral .NET operating-system checks so background work does not query
Unity editor state.

While an asynchronous Git mutation writes below `Packages/`, automatic asset
refresh is temporarily suspended. Completion is polled independently of the
window lifecycle, validation and rollback finish first, and auto-refresh is
restored in a `finally` path before one explicit refresh. This prevents a
mid-clone domain reload from orphaning the operation.

Every mutation has a worker-owned completion outcome: succeeded, failed with a
verified rollback, or failed with repository state requiring inspection. That
safety outcome finalizes the recovery journal independently of the EditorWindow
notification callback, so closing a window or encountering a GUI exception
cannot misclassify an already-verified repository result.

## Failure Handling

- CLI failures retain standard error for user-visible diagnostics.
- Timeouts cancel the process tree and retain a recovery warning whenever full
  termination cannot be confirmed.
- Standard output and error are bounded; any stream that cannot be completely
  drained is marked truncated, and structural parsers discard the partial data.
- Package addition validates the cloned root and rolls back invalid packages.
- Failed clones remove safe untracked worktrees and module metadata; ambiguous
  `.gitmodules` state is preserved and reported instead of being guessed at.
- Local metadata cleanup after a successful `git rm` is best-effort and warns
  rather than misreporting the already-completed tracked mutation.
- Window disable/re-enable generations prevent stale initial-load results from
  applying to a newer window lifecycle.
- Initial loading publishes Git and installed-package availability before the
  optional GitHub CLI check. A GitHub-only failure therefore never locks manual
  Git workflows, while repository mutations cancel or queue behind background
  readers without overtaking them.
- Closing the window cancels an in-progress GitHub browser authentication
  command and retains ownership until its process has terminated. A session
  marker also blocks a second hidden authentication process after domain reload
  until Unity is restarted.

## Testing Strategy

EditMode tests cover parsing, package-name and path validation, Git reference
validation, repository URL safety, Windows path quoting, GitHub clone URL
selection, discovery paging, search, and stale-request supersession.

Repository CI adds package metadata, documentation-link, Unity-meta, workflow,
and package-archive sanity checks that do not require a Unity license.
