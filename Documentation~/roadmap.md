# Roadmap

The `simplify-package-status` experiment tests a smaller role for Git Submodule
Manager: clear installed-package status and a native GitHub source view.

## Current Priorities

- Validate the simplified branch on Unity `7000.0.0a10` on macOS.
- Make installed submodules easy to distinguish from ordinary Unity packages.
- Keep **Sources > GitHub** limited to installed GitHub packages.
- Keep submodule detection and Unity's embedded-removal guard small and tested.
- Check native list and details behavior after refresh and script reload.

The existing compatibility gate retains exact Unity `6000.3.22f1`,
`6000.5.*f1` final releases, exact `6000.6.0f1` and `6000.6.2f1`, exact
`6000.7.0b1` and `6000.7.0b2` betas, and exact `7000.0.0a7`, `7000.0.0a8`,
`7000.0.0a9`, and `7000.0.0a10` alphas. Each target still needs verification
against the simplified implementation before a release claim.

## Product Boundary

Installation, updates, conversion, dependency resolution, and removal remain
with Unity and Git. Remote GitHub discovery, account setup, Welcome and
Preferences surfaces, and automatic repository operations are outside this
experiment.

Further work should make the status view more reliable or easier to understand.
Discuss additions through the [contribution guide](../.github/CONTRIBUTING.md)
before expanding its responsibilities.
