# Roadmap

Git Submodule Manager prioritizes predictable Git behavior, cross-platform
compatibility, and a native Unity Editor experience over feature count.

## Current Priorities

- maintain the 2.0 editor workflow on exact Unity `6000.3.22f1` and across
  Unity `6000.5.*f1` final patch releases;
- expand Windows, macOS, and Linux CI coverage where Unity licensing permits;
- improve update-state visibility without performing implicit network work;
- continue expanding integration coverage with temporary Git repositories;
- improve accessibility, keyboard navigation, and narrow-window behavior.

## Candidate Work

- package version and commit comparison;
- explicit batch update with per-package confirmation and failure isolation;
- dependency visualization for submodule packages;
- additional Git hosting providers in discovery views.

## Non-Goals

- replacing Git or credential managers;
- silently initializing, updating, or mutating repositories on editor startup;
- installing system tools from inside Unity;
- managing subtrees, registries, or arbitrary project folders;
- storing GitHub tokens or Git credentials.

The roadmap is directional rather than a delivery schedule. Before starting a
large change, follow the [contribution guide](../.github/CONTRIBUTING.md) so its
design and compatibility impact can be discussed first.
