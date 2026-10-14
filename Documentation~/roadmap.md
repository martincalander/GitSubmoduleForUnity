# Roadmap

Git Submodule Manager prioritizes predictable Git behavior, cross-platform
compatibility, and a native Unity Editor experience over feature count.

## Current Priorities

- maintain the current editor workflow on exact Unity `6000.3.22f1`, across
  Unity `6000.5.*f1` final patch releases, exact Unity `6000.6.0f1` and `6000.6.2f1`,
  the exact `6000.7.0b1` and `6000.7.0b2` betas, and the exact `7000.0.0a7`, `7000.0.0a8`, and `7000.0.0a9` alphas;
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
