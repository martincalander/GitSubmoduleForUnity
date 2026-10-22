# Roadmap

Git Submodule Manager has a focused role: show how packages are installed and
make installed submodules and GitHub packages easy to find in Unity's native
Package Manager.
The `main` branch develops this status and sources view. The former management
workflows are preserved on
[`legacy`](https://github.com/martincalander/GitSubmoduleForUnity/tree/legacy).

## Current Priorities

- Keep submodules clearly distinguishable from ordinary Unity packages.
- Keep **Sources > GitHub** limited to installed GitHub packages.
- Keep the **Repository** link in Unity's native links row and derive its URL
  from the installed package's existing metadata.
- Keep uncertain classification unverified without automatic repair or added
  Git reads.
- Preserve a small Editor footprint, with no continuous window scanning or
  background network discovery.
- Keep classification and embedded-folder deletion protection reliable across
  refreshes, script reloads, and unusual Git layouts.
- Verify compatibility on the exact Unity targets before releasing this design.

The compatibility gate retains exact Unity `6000.3.22f1`, `6000.5.*f1` final
releases, exact `6000.6.0f1` and `6000.6.2f1`, exact `6000.7.0b1` and
`6000.7.0b2` betas. All canonical `7000.0.0a*` alpha revisions with a positive
integer suffix are eligible for the existing runtime API checks. Keep these
per-feature checks independent as Unity evolves. Each exact target and platform
still needs verification against the implementation being released before a
release claim. See the [verification records](../.github/CONTRIBUTING.md) for exact
local coverage.

## Product Boundary

Direct installation from a Git URL and safe submodule package removal live in
Unity's native Package Manager. Updates, conversion, and dependency planning
stay with Unity and Git. Remote GitHub discovery, account setup, Welcome and
Preferences surfaces, and automatic repository operations are outside the
current product scope. Published `0.8.1` artifacts retain those legacy workflows.

Further work should make the status view more reliable, easier to understand,
or less expensive to run. Performance claims need measurements; the existing
startup comparison did not establish a meaningful Editor startup speedup.
Discuss additions through the [contribution guide](../.github/CONTRIBUTING.md)
before expanding its responsibilities.
