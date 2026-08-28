# Repository Publication Checklist

Use this checklist when publishing or transferring Git Submodule Manager. These
settings are not stored in Git and must be configured in GitHub.

## General

- [ ] Confirm the repository owner and default branch are correct.
- [ ] Add the description: `Manage Git-hosted UPM packages in Unity as editable submodules or read-only dependencies.`
- [ ] Add topics: `unity`, `unity-editor`, `upm`, `git`, `submodule`,
  `package-manager`, `developer-tools`.
- [ ] Use a project-specific social preview, or leave it unset.
- [ ] Confirm GitHub recognizes the MIT license.
- [ ] Enable Issues so the bundled forms become available.

## Branch Protection

Protect `main` with:

- [ ] pull requests required before merging;
- [ ] one approving review and Code Owner review when a second active maintainer
  is available;
- [ ] stale approvals dismissed when new commits are pushed;
- [ ] conversation resolution required;
- [ ] `Required sanity gate` required from the **Sanity Checks** workflow, with
  GitHub Actions selected as the expected source;
- [ ] force pushes and branch deletion blocked;
- [ ] administrator bypass limited to emergencies.

The required gate aggregates the package job and the complete Linux, macOS, and
Windows portability matrix. Do not require only `Validate UPM package`, because
that would allow a platform-specific failure to be merged.

Protect release tags with a `v*` tag ruleset:

- [ ] restrict tag creation to the release maintainer;
- [ ] block tag updates and deletion;
- [ ] do not allow bypass except for documented emergency recovery.

## Security and Automation

- [ ] Audit direct collaborators and remove `write` access from anyone who is
  not authorized to publish releases; use `triage` or `read` for issue and
  review participation.
- [ ] Enable Dependabot alerts and security updates.
- [ ] Enable secret scanning and push protection when available.
- [ ] Enable CodeQL scanning for the C# package and workflow files.
- [ ] Confirm Actions permissions default to read-only.
- [ ] Restrict Actions to the required publishers/actions and require
  full-length commit SHA pinning where the repository setting is available.
- [ ] Allow the **Publish Release** workflow to write repository contents only
  for its release job.
- [ ] Enable immutable releases before publishing the first release.
- [ ] Enable GitHub private vulnerability reporting and keep the security email
  in `.github/SECURITY.md` monitored as a fallback.
- [ ] Create the referenced labels (`needs-triage`, `support`, `bug`,
  `enhancement`, `documentation`, `dependencies`, `automation`, `maintenance`,
  `feature`, and `fix`) before enabling Issues or release automation, then
  confirm all three issue forms can be submitted.

## Release

1. Confirm immutable releases are enabled and release-capable repository access
   is limited to authorized maintainers.
2. Follow the maintainer process in [RELEASING.md](RELEASING.md).
3. Update `package.json` and `CHANGELOG.md`.
4. Merge through a green pull request.
5. Create and push an annotated `v<package-version>` tag.
6. Confirm **Publish Release** creates the GitHub release, UPM `.tgz`, and
   `SHA256SUMS` asset.
7. Test the released tag from a clean Unity project.

## Visibility

Keep the repository private until the documentation, license, branch
protection, issue handling, and release settings above are ready. Changing
visibility is an explicit owner decision.

If the repository is already public, do not publish a version tag or GitHub
release until the unchecked controls above have been completed.
