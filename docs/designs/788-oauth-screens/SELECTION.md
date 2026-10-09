# OAuth screens: selected directions

The maintainer's picks from `oauth-direction-lab.html`, as of 2026-10-09:

| Screen | Issue | Direction | Status |
| --- | --- | --- | --- |
| Login, continuing to consent | #798 | B, the "Next: approve <app>" box | Built in #798 |
| Consent | #798 | D, Compact card, with a prominent email | Built in #798 |
| Reconnect (an app already approved for everything it asks) | #798 | A slim password prompt in D's card | Built in #798 |
| Audit provenance | #800 | C, the actor line, with B's plug icon | Built in #800 (PR #1138) |
| Connected apps on Account | #799 | Pending | |
| The Owner's farm-wide view | #799 | Pending | |

## Terms

The UI says **Connected apps** for what OAuth calls clients, and **Disconnect** for revoking one, everywhere.

## Login B

At 1280 the box sits in the brand panel in place of the tagline. At 390 the same box sits above the form. The app name comes from the server before sign-in, and it is the app's own choice.

## Consent D, with the maintainer's change

D as drawn: the headline question, an **Unverified app** marker, two permission rows with icons, the return line, "Acts as you, with only your access" with a role chip, the password, Allow and Cancel, the undo line under the buttons, and a closed **Details** section with the longer explanations.

The maintainer's change: the signed-in account's email sits near the top in normal text colour and semibold weight, so it reads as which account is approving, not as a caption. **Not you? Sign out** is in Details, which keeps the visible words under budget.

The write permission reads "Record daily entries" with no line about drafts or submitting. #809 adds that line once the write tool's behaviour is settled.

## Reconnect

When the payload says `alreadyApproved`, the card asks only for the password: the headline "Reconnect <app>?", the Unverified marker, one line, the password, Allow and Cancel, and the undo line. It shows no permission rows and no Details. Every connection still spends a step-up grant (`docs/decisions/798-oauth-consent.md`).
