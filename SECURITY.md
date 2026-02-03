# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |

## Security Considerations

### Command Execution

This package executes CLI commands (`git`, `gh`) using `System.Diagnostics.Process`. The following security measures are in place:

- Commands are executed with `UseShellExecute = false` to prevent shell injection
- Arguments are constructed programmatically, not from raw user input
- The package only executes known, whitelisted commands (`git`, `gh`)

### Authentication

- This package does not store any credentials
- GitHub authentication is handled entirely by the GitHub CLI (`gh`)
- No tokens or passwords are transmitted or stored by this package

### Network Access

- Repository discovery uses GitHub CLI, which handles all authentication
- Package validation uses GitHub API via `gh api` command
- No direct network requests are made by this package

### File System Access

- The package reads and writes only within the Unity project directory
- Submodules are installed under `Packages/` following Unity conventions
- The package reads `.gitmodules` and `package.json` files

## Reporting a Vulnerability

If you discover a security vulnerability:

1. **Do not** open a public issue
2. Email the maintainer directly at martin.calander@gmail.com
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact
   - Suggested fix (if any)

You can expect:
- Acknowledgment within 48 hours
- Status update within 7 days
- Credit in the fix (unless you prefer anonymity)

## Best Practices for Users

1. **Keep dependencies updated**: Ensure Git and GitHub CLI are up to date
2. **Review repositories**: Before installing packages, review the source code
3. **Use trusted sources**: Only install packages from repositories you trust
4. **Private repositories**: Be aware that collaborators need access to private repo submodules
