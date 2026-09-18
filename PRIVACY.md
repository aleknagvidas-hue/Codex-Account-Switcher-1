# Privacy

Codex Account Switcher is a local Windows application. The project does not include telemetry, analytics, advertising, crash uploading, or a project-operated backend.

## Data the application stores

The application stores data under `%LOCALAPPDATA%\CodexAccountSwitcher`:

- User-entered account labels
- User-selected colors
- Cached usage-window metadata
- DPAPI-encrypted authentication payloads

Labels are not derived from email addresses, OpenAI usernames, or authentication claims.

## Data used during switching

The application reads and atomically replaces the active Codex authentication file at `%USERPROFILE%\.codex\auth.json`. It may create `%USERPROFILE%\.codex\auth.json.codex-switcher.bak` for rollback.

These files are local. They are not uploaded to infrastructure operated by this project.

## Network behavior

Authentication and usage requests are delegated to the Codex App Server installed with Codex. Browser sign-in and OpenAI service communication are therefore subject to the behavior and policies of the installed Codex application and OpenAI services.

## What not to share

Do not include any of the following in GitHub issues, screenshots, or support requests:

- `auth.json`
- `*.auth.dpapi`
- Access or refresh tokens
- Account IDs
- Email addresses
- Unredacted paths or logs that identify another person

## Removing local data

Exit the application and delete `%LOCALAPPDATA%\CodexAccountSwitcher`. This does not delete the OpenAI account or uninstall Codex.
