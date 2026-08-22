# Roadmap

Git Submodule Manager prioritizes predictable Git behavior, cross-platform
compatibility, and a native Unity Editor experience over feature count.

## Current Priorities

- validate the 1.0 editor workflow across Unity 6000.5 patch releases;
- expand Windows, macOS, and Linux CI coverage where Unity licensing permits;
- improve update-state visibility without performing implicit network work;
- add focused integration tests around temporary Git repositories;
- improve accessibility, keyboard navigation, and narrow-window behavior.

## Candidate Work

- package version and commit comparison;
- explicit batch update with per-package confirmation and failure isolation;
- dependency visualization for submodule packages;
- additional Git hosting providers in discovery views;
- opt-in organization presets and repository filters.

## Non-Goals

- replacing Git or credential managers;
- silently initializing, updating, or mutating repositories on editor startup;
- installing system tools from inside Unity;
- managing subtrees, registries, or arbitrary project folders;
- storing GitHub tokens or Git credentials.

The roadmap is directional, not a delivery commitment. Discuss proposals before
starting large implementations so design and compatibility constraints can be
agreed first.
