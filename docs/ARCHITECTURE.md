# Architecture

## Overview

Codex Account Switcher is a Windows-only WPF application targeting .NET 8. It keeps encrypted copies of explicitly added Codex authentication payloads and activates one profile at a time by replacing the local authentication file used by the installed Codex desktop app.

```text
User action
  -> WPF confirmation
  -> verified Codex shutdown
  -> save departing profile
  -> verify shutdown again
  -> atomic auth.json replacement
  -> verify active account fingerprint
  -> relaunch packaged Codex app
```

## Components

### `ProfileStore`

Stores profile metadata in `%LOCALAPPDATA%\CodexAccountSwitcher\profiles.json` and credential payloads in `profiles\<id>.auth.dpapi`. Credential writes are protected with DPAPI and use temporary-file replacement.

### `DpapiProtector`

Wraps Windows `CryptProtectData` and `CryptUnprotectData` with application-specific entropy. Protection is bound to the current Windows user context.

### `AuthIdentity`

Validates authentication JSON and derives a stable account fingerprint. It prefers stable account claims from supported token payloads and falls back to a file hash when required.

### `AuthSwitchService`

Coordinates shutdown, departing-profile persistence, atomic replacement, rollback, and relaunch. Authentication replacement occurs only after process shutdown is confirmed.

### `CodexProcessService`

Enumerates `ChatGPT.exe` processes and accepts only executables under an `OpenAI.Codex_*` WindowsApps package path. Shutdown requires consecutive empty scans and handles late process arrivals within a bounded timeout. Unidentified processes fail closed.

### `CodexAppServerClient`

Starts the Codex App Server in a unique temporary `CODEX_HOME`. It supports isolated browser sign-in and best-effort usage-window reads. Temporary authentication data is removed after the operation.

## Trust boundaries

| Boundary | Control |
| --- | --- |
| User to switcher | Explicit labels and confirmation dialogs |
| Switcher to saved profiles | DPAPI and fingerprint validation |
| Switcher to active Codex auth | Same-directory atomic replacement and rollback backup |
| Switcher to desktop processes | Package-path identity check and stable shutdown verification |
| Switcher to App Server | Isolated temporary `CODEX_HOME` and bounded protocol handling |
| Public support channels | No credentials, account IDs, email addresses, or unredacted authentication evidence |

## Failure behavior

The application fails before modifying `auth.json` when it cannot validate the target profile, identify the Codex process, or establish stable shutdown. Replacement failures attempt to restore the previous authentication file. Usage-read failures are isolated from switching and surface as unavailable metadata.

## Compatibility boundary

The installed Codex package and App Server are external dependencies. Their executable layout, protocol methods, and response schemas may change. A successful build proves source compatibility with the current .NET toolchain; it does not guarantee compatibility with future Codex releases.
