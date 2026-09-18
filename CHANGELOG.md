# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

- Removed the artificial four-account storage and display limit; all saved accounts now appear in the scrollable panel
- Saved-account switching no longer starts a browser login or depends on a private website session
- Switch installs the selected encrypted saved login directly and restarts both the desktop GUI and detached App Server
- Added a single-instance guard so manual launch and Windows startup cannot create competing panels
- Fixed reopening from the launcher so it restores the real hidden account panel instead of the notification-area helper window
- Recover from a stale switcher process that has lost both its account panel and notification-area window

### Added

- Privacy-safe three-profile screenshot in the README

### Changed

- Usage timestamps now remain English regardless of the Windows display locale
- The default window and usage column are wider so reset times remain readable
- Windows switching now follows the upstream close, replace, restart sequence and does not call `account/logout`, which can crash the Windows Desktop client
- Hidden packaged Codex renderer processes are terminated after the graceful shutdown timeout so they cannot keep the previous account active
- Isolated browser profiles are used only while adding accounts; ordinary switching is browser-free

## 1.0.0 - 2026-08-19

### Added

- Local storage of multiple Codex sessions under user-defined private labels
- Windows DPAPI protection for saved authentication payloads
- Isolated browser sign-in for adding another account
- Manual, confirmed account switching with atomic authentication replacement
- Codex process identity checks, stable shutdown verification, and bounded restart handling
- Best-effort short-term and weekly usage display
- One-click Windows x64 build script
- Deterministic and optional integration test harnesses
