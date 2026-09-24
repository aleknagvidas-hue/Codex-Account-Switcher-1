# Account Switcher Project Plan

## Goal

Keep one reliable Account Switcher that opens correctly, supports unlimited saved accounts, tracks each account's month, and provides quick access to its purchase source.

## Finished means

The final local app opens from the Account launcher, tracks each account's month, lets the owner save or edit an optional purchase link, opens safe web links from the account card, and passes the full test and packaged-app checks.

## Steps

- [x] Confirm `Codex-Account-Switcher` is the authoritative beta.22 source and inspect the saved account format.
- [x] Confirm existing saved accounts already contain exact added timestamps.
- [x] Add the added date, one-month end date, and live time remaining to every account card.
- [x] Add automated checks for calendar-month calculations and expiry labels.
- [x] Build the new self-contained application package.
- [x] Check the packaged app opens visibly and renders the new membership information.
- [x] Replace the local runnable copy and launcher with the verified build.
- [x] Confirm the beta.23 window opens and loads all five saved accounts on this computer.
- [x] Add an optional purchase link to the add, save-current, and edit account forms.
- [x] Store the purchase link without changing existing account data.
- [x] Add a safe Seller link button to each account card.
- [x] Test valid links, blocked unsafe link types, saving, editing, and opening behavior.
- [x] Build and visually check beta.24.
- [x] Replace the local Account app with the verified beta.24 build and open it.
- [ ] **CURRENT — BLOCKED:** Enable Start with Windows after the managed environment stops denying the required registry write.

## Next action

Use **Edit** or **Sign in with another account** to add a purchase link when needed.

## Waiting on the user

Nothing is needed for the purchase-link feature; the earlier Start with Windows setting remains blocked by the managed registry restriction.
