## Summary

Describe the user-facing change and why it is needed.

## Verification

- [ ] Repository sanity checks pass.
- [ ] Unity compiles with no new warnings or errors.
- [ ] Focused EditMode tests pass.
- [ ] The changed workflow was tested manually.
- [ ] I tested every affected platform, or listed below any platform I could not
  test.

List exact commands, Unity versions, operating systems, and any skipped checks.

## Safety and Compatibility

- [ ] Submodule filesystem changes stay within validated direct
  `Packages/<reverse-domain-name>` children.
- [ ] User input is not passed through a shell.
- [ ] No credential storage, system installer execution, or implicit startup
  mutation was introduced.
- [ ] Documentation and `CHANGELOG.md` were updated when behavior changed.
- [ ] New Unity assets were imported and include generated `.meta` files.

## Visual Changes

Attach before/after images when the editor UI or documentation visuals changed.

## License

- [ ] I have the right to submit this contribution under the MIT License.
