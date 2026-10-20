# Two-factor authentication screen directions

This throwaway lab supports the maintainer's screen picks for [#1275](https://github.com/mforce/cluckwork/issues/1275), part of [the TOTP milestone](https://github.com/mforce/cluckwork/issues/1273). It changes no application code and calls no API. The recommendations below are proposals. The maintainer's selections belong in `SELECTION.md` after review.

Open `two-factor-lab.html` to compare screens, directions, states, widths and themes. Buttons simulate the flows. Copy controls show preview feedback. Download produces a text file of generated sample codes. The QR illustration is deliberately not scannable. No working key, code or credential appears in the lab.

The lab reads the actual [colour tokens](../../../web/src/styles/tokens.css). Its controls follow [the MUI mapping](../822-mui-revamp.md), [the visual language](../864-visual-language/DIRECTION.md) and the current `FarmThemeProvider` overrides. It keeps Inter body text, Georgia page titles, outlined fields, the 4/8/12px radius hierarchy, status words with dots and centred phone dialogs. The auth frame follows the current [AuthShell](../../../web/src/components/AuthShell.tsx). Consent uses the selected compact card in [ConnectPage](../../../web/src/routes/ConnectPage.tsx), including permission checkboxes. The Users page retains the selected-person inspector and the horizontal phone ledger.

## Recommended directions

| Screen | Directions drawn | Recommendation and trade-off |
| --- | --- | --- |
| Account / Security | A. Existing accordion with status and management rows. | A keeps the status, remaining codes, Regenerate and Turn off together. No placement fork because the issue specifies Security on Account. |
| Enrolment | A. QR, optional manual key and confirmation field on one page. B. Scan and confirmation on separate steps. | A avoids a page change while switching between Cluckwork and an authenticator app. B shortens each step on a phone but requires another Continue action. |
| Recovery codes | A. Code list, copy, download and saved confirmation. | A keeps the once-only list visible until the user confirms saving it. No alternative changes a useful decision here. |
| Login code | A. The chosen Login B card. | A preserves the account, farm and connected-app context while replacing password entry with a code. A single input supports paste and one-time-code autofill. |
| Step-up | A1. Inline code alone. A2. Inline password plus code. B. Review the action, then verify. | A keeps the target and proof together, matching today's inline step-up. B reduces the first form's field count but hides the draft during verification. Consent B uses a separate confirmation dialog; A keeps proof in the compact card. |
| Reset another user | A. Reset in the selected-user inspector, then one confirmation with the acting user's code. | A keeps a destructive action beside the person it affects. The confirmation distinguishes Ben, the target, from Ana, the signed-in Admin. |

The app displays the Owner role as **Admin**. These frames use that existing label. The reset flow is an Owner action, not a new permission for other roles.

## State coverage

Every state renders at 1280 and 390 CSS pixels. Recommended direction A also renders every state in night mode at both widths. For step-up, A1 has night frames and A2 is light only.

- Account shows off, set-up unfinished, on, one recovery code left, regenerate confirmation, turn-off confirmation, a rejected proof and turn-off success.
- Enrolment shows the QR, the manual key, confirmation, an incorrect code and expired set-up. Direction A keeps scan and confirmation together. Direction B splits them.
- Recovery shows the initial set, copy feedback, download feedback, saved confirmation and a regenerated set that replaces the old codes.
- Login shows authenticator and recovery entry, each rejected, expiry and rate limiting. Connected-app variants keep Farm Ledger visible through errors, expiry, rate limiting, recovery entry and the return to the password. The long-name state checks app and account wrapping.
- Step-up shows the user-administration form and consent, incorrect proof and rate limiting in each. Both directions show a verification step; A keeps it inline. A1 shows code alone. A2 adds the signed-in user's current password in the same form. The proof policy is pending item 11 of the design record; A2 is a required combination, not a password fallback.
- Users shows selection, reset confirmation, incorrect proof, rate limiting, reset success and the caller's own row. The self row sends the user to Account / Security instead of offering an administrative self reset.

`TERMS` at the start of the lab script holds the three shared terms. [The design record](https://github.com/mforce/cluckwork/issues/1274) controls the final login and step-up rules. These mockups do not settle lifetimes, rate-limit durations, code count, reset session effects or the later Owner enrolment requirement. Ten generated sample codes and the remaining-code counts illustrate the layout only. Samples use Identity's five-character groups separated by a hyphen, with the alphabet in [UserManager.CreateTwoFactorRecoveryCode](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/Extensions.Core/src/UserManager.cs#L2291-L2326). The lab generates them in memory when the page opens, so day and night frames from one render run use the same examples. No code literal is committed.

The night-mode primary button matches the live app. `FarmThemeProvider.tsx` maps `palette.primary.main` to `--brand` and `contrastText` to `--on-brand`. `farmThemeOverrides.ts` leaves contained buttons on that background in night mode. The lab retains those exact tokens, even where the fill looks dark against the card.

## Existing behaviour to preserve

These groups came from a search of the current test files before the layouts were built. This lab changes none of them. The application slices must retain their guarantees and extend their expectations for an enrolled user as the design record requires.

| Tests found | Design treatment |
| --- | --- |
| `AccountPage.test.tsx`: `uses the Focus-panel width and opens Change password initially`; `changes the password and reports the other devices were signed out`. | Keep the 760px content width and existing password panel. The lab collapses unrelated panels to focus the Security frames. Production must retain the existing initial expansion. |
| `Login.test.tsx`: `uses the flat inset Field Console auth frame`; `returns to the consent route with its request after sign-in`; `names the app it continues to when sign-in leads to consent`; `shows a rate-limit message on a 429 and stays on /login`. | Keep the chosen auth frame, return request and app name throughout the code step. Add code-specific errors without changing the existing password-stage defences. |
| `ConnectPage.test.tsx`: `names the signed-in account prominently, not as a muted caption`; `spends a step-up grant on Allow and sends the browser to the app`; `keeps the choice after a wrong password`; `locks the choice while the password and the approval are checked`. | Keep the prominent email, selected permissions and grant consumption. A1 replaces current-password entry with an authenticator field. A2 retains the password and adds the code, pending the design record. Both directions keep the same request in view. |
| `UsersPage.test.tsx`: `keeps edit and role in the row and every action in the selected user's inspector`; `sets a user's password from the inspector action, sending id + password + a key`; `keeps the dialog open and shows the error when the server rejects it`; `does not let a dismissed password-reset continuation write`. | Add reset to the inspector. Keep errors in the active dialog and preserve its session boundary. Change only the enrolled caller's proof field in Set password. |
| `UsersPage.test.tsx`: `never stores the entered step-up password: reopening after a successful use shows it empty`; `clears any half-entered step-up password the instant the session ends`. | Retain transient proof entry. Authenticator input must clear on submission, close and logout in the real implementation. |

All test paths above are under `web/src/routes/`. No production test is retired by this design.

## Render and inspect

Run at the repository root:

```sh
node docs/designs/1275-two-factor/shoot.mjs
```

The script uses the local Playwright and Inter installs, then the shared checkout's installs if this worktree has none. `PLAYWRIGHT_DIR`, `INTER_WOFF2` and `CHROMIUM_PATH` override those locations. A direct HTML open without the font install uses the system fallback; the render script embeds the installed Inter font.

Frames go in the ignored `renders/` directory beside the lab. Pass another directory as the first argument if needed. The script writes `manifest.json` with the screen, direction, width, state, theme and actual height for each frame. Names follow `<screen>-<direction>-<width>-<state>-<light|dark>.png`.

The output uses `deviceScaleFactor: 1` and screenshot scale `css`. Long pages retain their full height; 390px captures are not shrunk to fit 844px. Dialog captures use the centred dialog and its own scroll area. The script checks horizontal frame overflow and exercises saved-code confirmation, enrolment, dialog dismissal, the connected-app recovery switch and the reset flow. It records no claim about real authentication or clipboard access.

Images stay outside git and attach to the issue only after the coordinator's review. This lab is a design instrument, not production UI.
