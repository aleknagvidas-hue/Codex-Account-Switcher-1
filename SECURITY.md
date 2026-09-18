# Security Policy

## Supported versions

Security fixes are applied to the latest commit on the `main` branch. Older source snapshots and third-party binary builds are not supported.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository. Do not disclose a suspected credential-handling vulnerability in a public issue before a fix is available.

Include only the minimum information required to reproduce the issue:

- Affected commit
- Windows and Codex versions
- Expected and observed behavior
- Reproduction steps using synthetic credentials whenever possible

Never attach real `auth.json` files, DPAPI profile files, access tokens, refresh tokens, account IDs, email addresses, or unredacted diagnostic output.

## Security boundaries

- Saved profile credentials are protected with Windows DPAPI for the current Windows user.
- The active Codex `auth.json` and local rollback copy remain in the format Codex requires and are not encrypted by this application.
- DPAPI does not protect a machine that is compromised by malware running as the same user or by an administrator.
- This project does not operate a backend and does not receive user credentials.
- The project is not a credential-sharing, subscription-pooling, usage-limit-bypass, or automatic account-rotation service.

## Binary trust

The repository intentionally focuses on source. If you use a binary produced by another party, verify its source and build provenance. Prefer building locally with `BUILD_RELEASE.cmd`.
