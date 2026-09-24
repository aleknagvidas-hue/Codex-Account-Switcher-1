# Changelog

All notable changes to this project will be documented in this file.

## Unreleased

- Added an optional purchase-source link to account creation and editing, with a safe seller-site button on each saved account card
- Added the exact date and time each account was saved, the date one calendar month ends, and a live days/hours remaining badge
- Added a dedicated Account app icon and a single final desktop-launch workflow
- Newly added accounts are now revealed automatically, and reopening the app reloads profiles from disk
- Added reset-order ranking, large numbered account cards, colored countdown clocks, and clear available/waiting states
- Accounts now sort by current usability and earliest blocking reset while keeping exact 5-hour and weekly reset times visible
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
