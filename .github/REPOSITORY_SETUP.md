# Repository Publication Checklist

Use this checklist when publishing or transferring Git Package Manager. These
settings are not stored in Git and must be configured in GitHub.

## General

- [ ] Confirm the repository owner and default branch are correct.
- [ ] Add the description: `A safe, cross-platform Unity Editor workflow for managing Git submodules as UPM packages.`
- [ ] Add topics: `unity`, `unity-editor`, `upm`, `git`, `submodule`,
  `package-manager`, `developer-tools`.
- [ ] Leave the social preview unset unless a human-designed, project-specific
  image is available; do not use a generic generated banner.
- [ ] Confirm GitHub recognizes the MIT license.
- [ ] Enable Issues so the bundled forms become available.

## Branch Protection

Protect `main` with:

- [ ] pull requests required before merging;
- [ ] at least one approving review;
- [ ] stale approvals dismissed when new commits are pushed;
- [ ] conversation resolution required;
- [ ] `Validate UPM package` required from the **Sanity Checks** workflow;
- [ ] force pushes and branch deletion blocked;
- [ ] administrator bypass limited to emergencies.

## Security and Automation

- [ ] Enable Dependabot alerts and security updates.
- [ ] Enable secret scanning and push protection when available.
- [ ] Confirm Actions permissions default to read-only.
- [ ] Allow the **Publish Release** workflow to write repository contents only
  for its release job.
- [ ] Add a private vulnerability reporting channel or keep the security email
  in `.github/SECURITY.md` monitored.

## Release

1. Follow the maintainer process in [RELEASING.md](RELEASING.md).
2. Update `package.json` and `CHANGELOG.md`.
3. Merge through a green pull request.
4. Create and push an annotated `v<package-version>` tag.
5. Confirm **Publish Release** creates the GitHub release, UPM `.tgz`, and
   `SHA256SUMS` asset.
6. Test the released tag from a clean Unity project.

## Visibility

Keep the repository private until the documentation, license, branch
protection, issue handling, and release settings above are ready. Changing
visibility is an explicit owner decision.
