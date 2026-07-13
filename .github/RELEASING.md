# Releasing Git Package Manager

This guide is for maintainers publishing a GitHub and UPM release. Releases
follow [Semantic Versioning](https://semver.org/) and are produced by the
[Publish Release workflow](workflows/release.yml).

## Release Authority

Only maintainers identified in [MAINTAINERS.md](MAINTAINERS.md) with repository
release access may publish a release. Release tags and GitHub release assets
must originate from the canonical repository.

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
   [`CHANGELOG.md`](../CHANGELOG.md).
4. Confirm installation, compatibility, and troubleshooting documentation are
   still accurate.
5. Run the repository checks:

   ```bash
   python3 .github/scripts/validate_repository.py
   npx --yes markdownlint-cli2@0.23.0 "**/*.md" "#Library" "#Temp"
   npm pack --dry-run
   ```

6. In a clean Unity project, install the package from the exact release commit,
   run `MartinCalander.GitPackageManager.Editor.Tests` in EditMode, and exercise add, update,
   and remove behavior.
7. Record any platform or Unity version that could not be tested in the release
   pull request.
8. Merge the release pull request only after required checks and review pass.

## Tag and Publish

Create an annotated tag on the reviewed release commit:

```bash
git switch main
git pull --ff-only
git tag -a v1.0.1 -m "Git Package Manager 1.0.1"
git push origin v1.0.1
```

Pushing the tag starts the Publish Release workflow. The workflow:

1. validates the repository;
2. verifies that the tag matches `package.json`;
3. builds the UPM-compatible npm archive;
4. generates `SHA256SUMS`;
5. creates the GitHub release and attaches both assets.

The workflow can also be started manually for an existing tag. Manual dispatch
does not create or move a tag.

## Verify the Published Release

- Confirm the workflow completed successfully.
- Confirm the GitHub release points to the intended commit.
- Download the `.tgz` and compare it with `SHA256SUMS`.
- Inspect the archive and confirm development-only `.github` files are absent.
- Install the tag from a clean Unity project:

  ```text
  https://github.com/martincalander/com.martincalander.gitpackagemanager.git#v1.0.1
  ```

- Confirm the package imports, the editor window opens, and the documented
  minimum Unity version remains accurate.

If the package is registered with OpenUPM, confirm its registry page discovers
the new immutable tag after the GitHub release is available.

## Correcting a Release

Do not rewrite or reuse a published version or tag. If a release is defective:

1. document the impact;
2. prepare a new patch version;
3. repeat the normal validation and publication process;
4. mark the affected GitHub release as deprecated only when that context helps
   users.

For a security release, coordinate timing and disclosure through
[SECURITY.md](SECURITY.md).
