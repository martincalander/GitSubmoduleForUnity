## Summary

Describe the user-facing change and why it is needed. Target `main` for the
current installed-status and GitHub source view.

## Verification

- [ ] Repository sanity checks pass.
- [ ] Unity compiles with no new warnings or errors, if C# changed.
- [ ] Focused EditMode tests pass, if C# changed.
- [ ] Compatibility tests cover changed reflection or Harmony contracts.
- [ ] The full Editor assembly covers broad lifecycle changes.
- [ ] The changed behavior was tested manually, where applicable.
- [ ] I tested every affected platform, or listed below any platform I could not
  test.

List exact commands, Unity versions, operating systems, and any skipped checks.

## Safety and Compatibility

- [ ] Installed-state reads leave repositories, manifests, and lockfiles unchanged.
- [ ] Ordinary Unity package behavior and submodule deletion protection are
  preserved.
- [ ] No package-management workflow, background network discovery, or credential
  handling was added.
- [ ] No permanent window polling or unnecessary view rebuilds were introduced.
- [ ] Performance claims include measurements and their limits.
- [ ] User input is not passed through a shell.
- [ ] Documentation and `CHANGELOG.md` were updated when behavior changed.
- [ ] New Unity assets were imported and include generated `.meta` files.

## Visual Changes

Attach before/after images when the editor UI or documentation visuals changed.

## License

- [ ] I have the right to submit this contribution under the MIT License.
