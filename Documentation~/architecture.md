# Architecture and Safety Model

## Package Boundary

The package contains editor-only assemblies. No runtime assembly or player code
is emitted.

```text
Editor/
├── PackageManagerWindow.*      IMGUI rendering and user actions
├── DiscoveryCoordinator.cs    paged GitHub discovery state
├── RepositoryCoordinator.cs   lazy remote branch loading and cache
├── Models/                    package and repository data
└── Utilities/
    ├── CliCommandRunner.cs    process execution and async handles
    ├── CliInstaller.cs        install guidance only
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

## Mutation Boundary

Before a Git mutation, the utility validates:

- repository URL or local repository argument;
- branch reference syntax;
- reverse-domain package name;
- a direct destination below `Packages/`.

The tool refuses mutation outside `Packages/com.author.package`.

## Discovery State

GitHub discovery uses `gh api` with 50-item pages. Search is debounced and sent
to GitHub rather than performed over a full local account mirror. The
coordinator keeps one active page request and one newest pending request, so a
stale response cannot replace a newer owner, search, or page selection.

Remote `package.json` checks and branch listing are selection-driven and lazy.

## Threading

Blocking CLI work runs on background threads. Completion is published with a
volatile memory barrier before the editor thread consumes the result. Unity API
calls and UI mutations remain on the editor thread.

## Failure Handling

- CLI failures retain standard error for user-visible diagnostics.
- Timeouts terminate the child process on a best-effort basis.
- Package addition validates the cloned root and rolls back invalid packages.
- Local metadata cleanup after a successful `git rm` is best-effort and warns
  rather than misreporting the already-completed tracked mutation.
- Window disable/re-enable generations prevent stale initial-load results from
  applying to a newer window lifecycle.

## Testing Strategy

EditMode tests cover parsing, package-name and path validation, Git reference
validation, repository URL safety, Windows path quoting, GitHub clone URL
selection, discovery paging, search, and stale-request supersession.

Repository CI adds package metadata, documentation-link, Unity-meta, workflow,
and package-archive sanity checks that do not require a Unity license.
