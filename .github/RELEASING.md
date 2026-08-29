# Releasing Git Submodule Manager

This guide is for maintainers publishing a GitHub release for the UPM package.
Releases follow [Semantic Versioning](https://semver.org/) and are produced by
the [Publish Release workflow](workflows/release.yml).

## Release Authority

Only maintainers identified in [MAINTAINERS.md](MAINTAINERS.md) with repository
release access may publish a release. Release tags and GitHub release assets
must originate from the canonical repository. Audit direct collaborators before
release: GitHub `write` access includes release creation and editing, so people
without release authority should use `triage` or `read` access. Enable immutable
releases before publishing the first version so published tags and assets cannot
be replaced.

Before publishing, configure the protected `release` GitHub environment with
required maintainer reviewers and deployment restricted to protected `v*`
tags. Protect `main` from direct and force pushes, require pull requests and
sanity checks, and require independent review when another maintainer is
available. Add a tag protection rule for `v*` before publishing. These settings
are repository controls and cannot be enforced by workflow YAML alone.

Configure these values only in the protected `release` environment:

- environment secret `UPM_SERVICE_ACCOUNT_KEY_ID`;
- environment secret `UPM_SERVICE_ACCOUNT_KEY_SECRET`;
- environment variable `UPM_ORG_ID`.

The keys must belong to a dedicated Unity service account with only the
organization-level **Package Manager Package Signer** role. Follow Unity's
[UPM CLI prerequisites](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-cli-install.html#prereqs).
No Unity Editor license, email, or password belongs in GitHub Actions.

## Version Policy

- **Patch** releases contain backward-compatible fixes and documentation
  corrections.
- **Minor** releases add backward-compatible functionality.
- **Major** releases may contain incompatible behavior or API changes.
- Pre-release versions use valid SemVer identifiers such as `1.1.0-beta.1` and
  matching tags such as `v1.1.0-beta.1`.

The version in [`package.json`](../package.json) and the tag without its leading
`v` must match exactly.

## Prepare the Release

1. Work from a clean branch based on the latest `main`.
2. Update `package.json` to the intended version.
3. Move relevant entries from **Unreleased** into a dated version section in
   [`CHANGELOG.md`](../CHANGELOG.md), remove any unpublished-version note, and
   update its comparison links for the intended tag.
4. Confirm installation, compatibility, and troubleshooting documentation are
   still accurate.
5. Run the repository checks:

   ```bash
   python3 .github/scripts/validate_repository.py
   npx --yes markdownlint-cli2@0.23.0 "**/*.md" "#Library" "#Temp"
   npm pack --ignore-scripts --dry-run
   ```

6. Build the deterministic bootstrap, import that exact file into a clean
   supported Unity project, and exercise its recovery path. After the test,
   replace `Installer~/SHA256SUMS` with the exact SHA-256 and filename printed by
   the builder. Repository validation and the release workflow require the
   published bootstrap to match those locally tested bytes.
7. In a clean Unity project, install the package from the exact release commit,
   run `MartinCalander.GitSubmoduleManager.Editor.Tests` in EditMode, then test
   submodule and read-only installation, both eligible conversions, and
   submodule removal.
8. Record any platform or Unity version that could not be tested in the release
   pull request.
9. Merge the release pull request only after required checks pass and the
   applicable review policy is satisfied.

Hosted workflows do not launch the Unity Editor or run EditMode tests. Complete
and report the applicable Unity checks locally before tagging a release.

## OpenUPM Bootstrap

The workflow always creates a signed GitHub Release. Its OpenUPM job remains
disabled until the repository Actions variable `OPENUPM_ENABLED` is exactly
`true`.

For the first publication:

1. publish the first signed GitHub Release normally;
2. submit the package through [OpenUPM Add Package](https://openupm.com/packages/add/);
3. verify its `repoUrl` is the canonical public repository
   `https://github.com/martincalander/GitSubmoduleForUnity`;
4. ensure the generated metadata uses `trackingMode: githubRelease` and the
   stable asset prefix
   `githubReleaseAssetName: 'com.martincalander.gitsubmodulemanager-'`;
5. wait for the metadata pull request to merge and the initial signed version
   to become available;
6. create the repository Actions variable `OPENUPM_ENABLED=true` for future
   releases.

Initial registration is mandatory; the OpenUPM action cannot register a new
package. GitHub Release tracking makes OpenUPM publish the signed release asset
unchanged. Refer to OpenUPM's
[signed-package guide](https://openupm.com/docs/signing-upm-packages).

## Tag and Publish

Create an annotated tag on the reviewed release commit:

```bash
git switch main
git pull --ff-only
git tag -a v0.8.1 -m "Git Submodule Manager 0.8.1"
git push origin v0.8.1
```

Pushing the tag starts the Publish Release workflow. The workflow:

1. validates the repository;
2. verifies that the tag matches `package.json`;
3. proves the tagged commit is reachable from `origin/main`;
4. builds and checksums a credential-free source archive;
5. waits for approval in the protected `release` environment;
6. signs that validated payload with the pinned Unity UPM CLI;
7. requires the signing attestation and verifies every other packaged file is
   unchanged;
8. independently builds and byte-verifies the license-free Unity bootstrap,
   including its committed locally tested SHA-256;
9. creates the GitHub release with the signed archive, bootstrap installer, and
   combined checksum manifest;
10. when enabled, asks OpenUPM to publish the signed `.tgz` and verifies it
   recognizes the signature.

Pre-release SemVer tags are published as GitHub pre-releases.

If only the downstream OpenUPM job fails or times out after the GitHub Release
exists, use **Re-run failed jobs**. Do not re-run every job against an existing
immutable release.

## Verify the Published Release

- Confirm the workflow completed successfully.
- Confirm the GitHub release points to the intended commit.
- Download the `.tgz` and compare it with `SHA256SUMS`.
- Download the `.unitypackage`, compare it with `SHA256SUMS`, and import it in
  a clean supported Unity project.
- Confirm the archive contains the nonempty signing attestation
  `package/.attestation.p7m`.
- Inspect the archive and confirm development-only `.github` files are absent.
- Install the tag from a clean Unity project:

  ```text
  https://github.com/martincalander/GitSubmoduleForUnity.git#v0.8.1
  ```

- Confirm the package imports, **Package Manager > Sources > GitHub** opens, and
  the documented minimum Unity version remains accurate.

If OpenUPM automation is enabled, confirm its job reports the requested version
as published and signed.

## Correcting a Release

Do not rewrite or reuse a published version or tag. If a release is defective:

1. document the impact;
2. prepare a new patch version;
3. repeat the normal validation and publication process;
4. update the affected release notes to point users to the fixed version when
   that context is useful.

For a security release, coordinate timing and disclosure through
[SECURITY.md](SECURITY.md).
