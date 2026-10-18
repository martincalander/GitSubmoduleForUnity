# Support

## Before Asking for Help

Check the [troubleshooting guide](../Documentation~/troubleshooting.md), then
confirm that Git works in the same user account that launches Unity:

```bash
git --version
```

The simplified **Sources > GitHub** page uses installed project metadata.
GitHub CLI and authentication are not required.

## Where to Ask

Open the repository's **Issues** tab and choose the form that fits:

- **Support request** for setup questions and usage help;
- **Bug report** for reproducible defects;
- **Feature request** for scoped enhancements.

If the **Issues** tab is unavailable, the repository owner has not finished the
[publication setup](REPOSITORY_SETUP.md).

For vulnerabilities, follow [SECURITY.md](SECURITY.md). Do not publish
security-sensitive details in a public issue.

## Useful Diagnostic Information

Include the following when reporting a problem:

- Unity version and operating system;
- package version or commit;
- output from `git --version`;
- the exact operation, expected result, actual result, and complete error text;
- a minimal `.gitmodules` excerpt with private URLs redacted;
- screenshots when the problem is visual.

Please never include access tokens, private keys, credential-helper output, or
private repository URLs you are not allowed to disclose.
