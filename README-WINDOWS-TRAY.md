# Codex Account Switcher — Windows tray variant

This is a local Windows adaptation of [Chisiki1/codex-account-switcher-windows](https://github.com/Chisiki1/codex-account-switcher-windows). It keeps the upstream MIT license and adds a scrollable unlimited-account tray panel, cautious usage refresh, and stricter sign-in replacement.

## What this build does

- Shows every saved account profile in one scrollable panel, with no artificial account limit.
- Shows the available 5-hour and weekly remaining percentages and reset times.
- Labels readings as stale or unavailable instead of turning failures into zero remaining.
- Refreshes at a selectable interval of 2, 5, 10, or 15 minutes; 5 minutes is the default.
- Uses the installed Codex App Server's documented external-token mode for usage reads. The monitoring request receives an access token and account ID, never a refresh token, and runs in a temporary isolated `CODEX_HOME`.
- Encrypts saved sign-ins and recovery backups with Windows DPAPI for the current Windows user.
- Keeps Codex chats, projects, MCP servers, plugins, skills, and settings shared.

## Safety behavior

- Finish every running Codex task before switching. The utility cannot determine whether a task is active.
- The **Switch** button owns the complete transition: it closes the packaged Codex GUI and detached App Server processes, replaces the saved sign-in, and restarts Codex. It does not open a browser or call account-wide `account/logout`.
- Every replacement creates and verifies a unique encrypted recovery backup. Failure or cancellation after replacement restores only the backup created by that attempt.
- The active sign-in is still stored in Codex's required `auth.json` format. Windows account isolation and disk encryption remain relevant.
- No telemetry, remote control, automatic limit-based switching, account pooling, or credential syncing is included.

## Start the portable build

1. Extract the complete ZIP to a normal local folder.
2. Run `Start Codex Account Switcher.cmd`.
3. The panel opens as a normal taskbar window. Use the standard Windows close button, **Quit**, or **Exit app** to stop it completely. **Start with Windows** can be turned on or off from the panel.

The package includes Microsoft's official .NET 8 Windows Desktop runtime so no separate runtime installation is required. Keep the `runtime` and `app` folders beside the launcher.

The build is unsigned. Windows may identify it as an unknown publisher. Verify `SHA256SUMS.txt`; a checksum detects file changes but does not provide publisher identity. Do not bypass a security policy imposed by your organization.

## Add accounts cautiously

1. Save the currently active Codex account under a private label.
2. Add a second account through **Sign in with another account** and complete the browser sign-in yourself.
3. With all Codex tasks finished, test switching both ways and verify the active account before adding more accounts.
4. Use the **Switch** button for saved profiles. It installs the selected encrypted saved login directly, closes Codex completely, and restarts it automatically on that account. It does not alter your normal ChatGPT website session.

Account labels are local and need not be email addresses. Saved data is under `%LOCALAPPDATA%\CodexAccountSwitcher`; per-switch encrypted recovery backups are under `%USERPROFILE%\.codex\switcher-backups`.

## Monitoring compatibility

On startup the utility runs a synthetic, isolated compatibility probe against the installed Codex App Server. Background monitoring remains paused if the probe fails. Expired access tokens are not refreshed by the monitor; open that account in Codex to renew its sign-in, save it again, and refresh.

The implementation follows the documented `account/login/start` external-token mode and `account/rateLimits/read` method: https://learn.chatgpt.com/docs/app-server

## Source and validation

- Upstream reviewed commit: `cd5ac60a876aa5a6dec6df8f95546efa117368c6`.
- Variant version: 1.1.2-beta.20.
- Deterministic tests cover rollback isolation, cancellation recovery, no-force shutdown, usage parsing, token exclusion, serialized operations, backoff, and XAML construction.
- Integration tests use a synthetic token and isolated home to check the installed App Server; they never read or change the user's active sign-in.
- A successful local build and test do not prove future Codex App Server compatibility.
