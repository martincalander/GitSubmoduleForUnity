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

Before enabling credentialed CI, configure two protected GitHub environments:

- `unity-ci`, with required maintainer reviewers and environment-scoped
  `UNITY_EMAIL`, `UNITY_PASSWORD`, plus either `UNITY_LICENSE` or `UNITY_SERIAL`,
  and deployment branches restricted to protected `main` and protected `v*`
  tags;
- `release`, with required maintainer reviewers and deployment restricted to
  protected `v*` tags.

Do not store Unity credentials as general repository secrets. Set the repository
variable `UNITY_CI_ENABLED=true` only after `unity-ci` is protected. Protect
`main` from direct and force pushes, require pull requests and sanity checks,
and require independent review when another maintainer is available. Add a tag
protection rule for `v*` before publishing. These settings are repository
controls and cannot be enforced by workflow YAML alone.

Keep the GameCI action and each release-matrix Editor image pinned to reviewed
commit and image digests. Adding another Unity patch to CI requires recording
its exact image digest in both workflows before it can become a release gate.

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

6. In a clean Unity project, install the package from the exact release commit,
   run `MartinCalander.GitSubmoduleManager.Editor.Tests` in EditMode, then test
   submodule and read-only installation, both eligible conversions, and
   submodule removal.
7. Record any platform or Unity version that could not be tested in the release
   pull request.
8. Merge the release pull request only after required checks pass and the
   applicable review policy is satisfied.

Fork pull requests never receive Unity credentials. A maintainer must inspect
the contribution and manually dispatch **Sanity Checks** with its reviewed,
full 40-character commit SHA as the `ref` input to run the credentialed Unity
gate before merge. Branch names and abbreviated revisions are intentionally
rejected so the reviewed input cannot move before the job starts. Dispatch the
workflow definition from protected `main`, never from a contribution branch:

```bash
gh workflow run ci.yml --ref main \
  -f ref=<reviewed-40-character-commit-sha> \
  -f unity_version=6000.3.22f1
```

## Tag and Publish

Create an annotated tag on the reviewed release commit:

```bash
git switch main
git pull --ff-only
git tag -a v2.0.0 -m "Git Submodule Manager 2.0.0"
git push origin v2.0.0
```

Pushing the tag starts the Publish Release workflow. The workflow:

1. validates the repository;
2. verifies that the tag matches `package.json`;
3. proves the tagged commit is reachable from `origin/main`;
4. builds the UPM-compatible npm archive once and generates `SHA256SUMS`;
5. extracts and tests those exact archive bytes in Unity;
6. waits for approval in the protected `release` environment;
7. creates the GitHub release and attaches the tested assets.

Pre-release SemVer tags are published as GitHub pre-releases.

The workflow can also be started manually for an existing tag. Manual dispatch
does not create or move a tag. Start the workflow from the same protected tag
provided as its input so the `release` environment evaluates the reviewed
release ref:

```bash
gh workflow run release.yml --ref v2.0.0 -f tag=v2.0.0
```

## Verify the Published Release

- Confirm the workflow completed successfully.
- Confirm the GitHub release points to the intended commit.
- Download the `.tgz` and compare it with `SHA256SUMS`.
- Inspect the archive and confirm development-only `.github` files are absent.
- Install the tag from a clean Unity project:

  ```text
  https://github.com/martincalander/GitSubmoduleForUnity.git#v2.0.0
  ```

- Confirm the package imports, **Package Manager > Sources > GitHub** opens, and
  the documented minimum Unity version remains accurate.

If the package is registered with OpenUPM, confirm its registry page discovers
the new immutable tag after the GitHub release is available.

## Correcting a Release

Do not rewrite or reuse a published version or tag. If a release is defective:

1. document the impact;
2. prepare a new patch version;
3. repeat the normal validation and publication process;
4. update the affected release notes to point users to the fixed version when
   that context is useful.

For a security release, coordinate timing and disclosure through
[SECURITY.md](SECURITY.md).
