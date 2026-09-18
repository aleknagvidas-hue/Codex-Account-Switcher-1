# Contributing

Thank you for considering a contribution.

## Before opening an issue

1. Search existing issues.
2. Reproduce the problem with the latest `main` branch.
3. Remove credentials, account identifiers, email addresses, and personal paths from all evidence.
4. Use private vulnerability reporting for security-sensitive findings.

## Development setup

Requirements:

- Windows 10 or 11, x64
- .NET 8 SDK
- Windows Codex desktop app only for optional integration checks

Build:

```powershell
dotnet build .\CodexAccountSwitcher.sln -c Release
```

Run deterministic tests:

```powershell
dotnet run --project .\tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release
```

Run optional read-only integration checks:

```powershell
dotnet run --project .\tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release -- --integration
```

## Pull requests

- Keep changes focused and explain the user-visible behavior.
- Add or update tests for credential persistence, process handling, or switching changes.
- Preserve fail-closed behavior before authentication replacement.
- Do not add telemetry, credential logging, account sharing, automatic account rotation, subscription pooling, or limit-circumvention features.
- Do not commit generated binaries, `bin`, `obj`, runtime profiles, authentication files, logs, or local paths.
- Ensure all user-facing text and public documentation remain in English.

## Commit style

Use a short imperative subject, for example:

```text
Harden Codex shutdown verification
```
