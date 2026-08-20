# Support

## Before Asking for Help

Check the [troubleshooting guide](../Documentation~/troubleshooting.md), then
confirm these commands work in the same user account that launches Unity:

```bash
git --version
gh --version
gh auth status -h github.com
```

Only Git is required. GitHub CLI (`gh`) is optional and powers repository
discovery.

## Where to Ask

- Use the [support request form](https://github.com/martincalander/GitSubmoduleManager/issues/new?template=support_request.yml)
  for setup questions and usage help.
- Use the [bug report form](https://github.com/martincalander/GitSubmoduleManager/issues/new?template=bug_report.yml)
  for reproducible defects.
- Use the [feature request form](https://github.com/martincalander/GitSubmoduleManager/issues/new?template=feature_request.yml)
  for scoped enhancements.
- Follow [SECURITY.md](SECURITY.md) for vulnerabilities. Do not publish
  security-sensitive details in a public issue.

## Useful Diagnostic Information

Include the following when reporting a problem:

- Unity version and operating system;
- package version or commit;
- output from `git --version` and, when relevant, `gh --version`;
- whether `gh auth status -h github.com` succeeds;
- the exact operation, expected result, actual result, and complete error text;
- a minimal `.gitmodules` excerpt with private URLs redacted;
- screenshots when the problem is visual.

Please never include access tokens, private keys, credential-helper output, or
private repository URLs you are not allowed to disclose.
