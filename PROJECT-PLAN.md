# Account Switcher Project Plan

## Goal

Keep one reliable Account Switcher that opens correctly, supports unlimited saved accounts, and shows each account's one-month membership dates.

## Finished means

The final local app opens from the Account launcher, shows the exact date each account was added, shows the date one calendar month ends, shows the live time remaining, and passes the full test and packaged-app checks.

## Steps

- [x] Confirm `Codex-Account-Switcher` is the authoritative beta.22 source and inspect the saved account format.
- [x] Confirm existing saved accounts already contain exact added timestamps.
- [x] Add the added date, one-month end date, and live time remaining to every account card.
- [x] Add automated checks for calendar-month calculations and expiry labels.
- [x] Build the new self-contained application package.
- [x] Check the packaged app opens visibly and renders the new membership information.
- [x] Replace the local runnable copy and launcher with the verified build.
- [x] Confirm the beta.23 window opens and loads all five saved accounts on this computer.
- [ ] **BLOCKED:** Enable Start with Windows after the managed environment stops denying the required registry write.

## Next action

Tick **Start with Windows** once from an app instance launched normally outside the managed workspace.

## Waiting on the user

The membership feature is finished; only the earlier Start with Windows setting is waiting because Windows denied the registry change from this managed session.
