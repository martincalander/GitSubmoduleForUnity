# Project Governance

Git Submodule Manager is an open-source project led by Martin Calander. This
document explains how project decisions are made and how responsibility is
shared. Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Project Priorities

Decisions are evaluated in this order:

1. protect users and their repositories from data loss or unsafe mutations;
2. preserve compatibility across supported Unity versions, Windows, macOS,
   and Linux;
3. keep the package editor-only and focused on Git-backed UPM packages, with
   editable submodules limited to direct children of `Packages/`;
4. maintain clear, testable, and reviewable code;
5. improve performance where it does not compromise correctness or clarity.

## Roles

### Project lead

The project lead sets product direction, appoints maintainers, resolves final
decision deadlocks, and controls repository ownership and releases.

### Maintainers

Maintainers review changes, triage reports, protect the security and release
process, and make routine decisions within the documented project scope. The
current team and expectations are listed in [MAINTAINERS.md](MAINTAINERS.md).

### Contributors

Anyone following the Code of Conduct and
[contribution guide](CONTRIBUTING.md) may report problems, propose changes,
improve documentation, or submit pull requests. A merged contribution does not
automatically grant maintainer permissions.

## Decision Process

Routine bug fixes, documentation corrections, tests, and narrowly scoped
improvements are decided through pull-request review.

Changes affecting repository mutation, credential handling, supported
platforms, public APIs, licensing, package identity, or overall product scope
should first be discussed in an issue. Maintainers seek a practical consensus
based on reproducible evidence, compatibility, and the priorities above. When
consensus cannot be reached, the project lead makes the final decision and
records the reasoning in the issue or pull request.

Security reports follow [SECURITY.md](SECURITY.md), not the public issue
process. The project lead may privately coordinate and release an urgent fix
before opening the normal public discussion.

## Change Approval

- Changes normally enter `main` through a pull request.
- Required CI checks must pass before merge.
- Code changes should receive an independent maintainer review when one is
  available.
- When the project has only one active maintainer, the project lead may merge
  after required CI passes and must record the verification performed in the
  pull request.
- Authors do not approve their own changes.
- Force pushes to protected branches and rewriting published release tags are
  not part of the normal workflow.

Repository administrators may otherwise bypass the normal process only to
respond to a security incident, restore a broken release or automation path, or
recover the repository. The reason should be documented afterward when
disclosure is safe.

## Releases

Maintainers with release authority follow [RELEASING.md](RELEASING.md).
Published releases must correspond to an immutable Git tag and the matching
UPM package version.

## Governance Changes

Governance changes use the same review process as code changes. Significant
changes should be announced in an issue and approved by the project lead.
