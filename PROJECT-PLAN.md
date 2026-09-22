# Account Switcher Finalization Plan

## Goal

Keep one final Account Switcher that always opens the newest version, immediately shows newly added accounts, and has one desktop shortcut named **Account**.

## Finished means

The desktop shortcut opens the newest app, new accounts appear in that same app, old runnable copies are removed, and the final workflow is checked on this computer.

## Steps

- [x] Identify the main project folder and confirm the newest source version.
- [x] Trace every launcher, executable, saved-account location, and running copy.
- [x] Fix the add-account refresh problem in the newest source.
- [x] Build one final application folder from the newest source.
- [x] Replace every old runnable copy with beta.22 and redirect the old launcher to the final app.
- [ ] **CURRENT:** Put the prepared **Account** shortcut on the desktop with the application logo.
- [x] Check that the launcher opens the final version and reloads all saved accounts.
- [x] Commit the final source changes and deliver the working local result.
- [ ] **CURRENT:** Push the committed updates to the connected GitHub repository.

## Next action

Retry the GitHub push when network access is available.

## Waiting on the user

Windows blocked this task from writing to the Desktop or startup registry, so `Account.lnk` still needs to be copied to the Desktop and **Start with Windows** must be ticked once in the app. This environment also cannot connect to GitHub over HTTPS, so the push is waiting on network access.
