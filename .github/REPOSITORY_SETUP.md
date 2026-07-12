# Repository Publication Checklist

Use this checklist when publishing or transferring Git Package Manager. These
settings are not stored in Git and must be configured in GitHub.

## General

- [ ] Confirm the repository owner and default branch are correct.
- [ ] Add the description: `A safe, cross-platform Unity Editor workflow for managing Git submodules as UPM packages.`
- [ ] Add topics: `unity`, `unity-editor`, `upm`, `git`, `submodule`,
  `package-manager`, `developer-tools`.
- [ ] Upload `Documentation~/Images/Brand/social-preview.png` as the social
  preview.
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
  in `SECURITY.md` monitored.

## Release

1. Update `package.json` and `CHANGELOG.md`.
2. Merge through a green pull request.
3. Create and push an annotated `v<package-version>` tag.
4. Confirm **Publish Release** creates the GitHub release, UPM `.tgz`, and
   `SHA256SUMS` asset.
5. Test the released tag from a clean Unity project.

## Visibility

Keep the repository private until the documentation, license, branch
protection, issue handling, and release settings above are ready. Changing
visibility is an explicit owner decision.
