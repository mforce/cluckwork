# Two-factor authentication screen directions

This throwaway lab supports my screen picks for [#1275](https://github.com/mforce/cluckwork/issues/1275), part of [the TOTP milestone](https://github.com/mforce/cluckwork/issues/1273). It changes no application code and calls no API. I picked Enrolment A and Step-up A on 2026-10-21. The coordinator also asked to record the four single-design screens. [SELECTION.md](SELECTION.md) records the picks, their sources and the representative frame names.

Open `two-factor-lab.html` to compare screens, directions, states, widths and themes. Buttons simulate the flows. Copy controls show preview feedback. Download produces a text file of generated sample codes. The QR illustration is deliberately not scannable. No working key, code or credential appears in the lab.

The lab reads the actual [colour tokens](../../../web/src/styles/tokens.css). Its controls follow [the MUI mapping](../822-mui-revamp.md), [the visual language](../864-visual-language/DIRECTION.md) and the current `FarmThemeProvider` overrides. It keeps Inter body text, Georgia page titles, outlined fields, the 4/8/12px radius hierarchy, status words with dots and centred phone dialogs. The auth frame follows the current [AuthShell](../../../web/src/components/AuthShell.tsx). Consent uses the selected compact card in [ConnectPage](../../../web/src/routes/ConnectPage.tsx), including permission checkboxes. The Users page retains the selected-person inspector and the horizontal phone ledger.

## Selected directions and alternatives

| Screen | Directions drawn | Selection and trade-off |
| --- | --- | --- |
| Account / Security | A. Existing accordion with status and management rows. | A keeps the status, remaining codes, Regenerate and Turn off together. No placement fork because the issue specifies Security on Account. |
| Enrolment | A. QR, optional manual key and confirmation field on one page. B. Scan and confirmation on separate steps. | A avoids a page change while switching between Cluckwork and an authenticator app. B shortens each step on a phone but requires another Continue action. |
| Recovery codes | A. Code list, copy, download and saved confirmation. | A keeps the once-only list visible until the user confirms saving it. No alternative changes a useful decision here. |
| Login code | A. The chosen Login B card. | A preserves the account, farm and connected-app context while replacing password entry with a code. A single input supports paste and one-time-code autofill. |
| Step-up | A2. Selected inline password plus code. B. Review the action, then verify. A1. Superseded code alone. | Selected A keeps the target and proof together, matching today's inline step-up. B reduces the first form's field count but hides the draft during verification. Consent B uses a separate confirmation dialog; A keeps proof in the compact card. |
| Reset another user | One inspector layout. A2 supplies password-plus-code confirmation. A1 proof is superseded. | The inspector keeps a destructive action beside the person it affects. The confirmation distinguishes Ben, the target, from Ana, the signed-in Admin. |

The app displays the Owner role as **Admin**. These frames use that existing label. The reset flow is an Owner action, not a new permission for other roles.

## State coverage

Every state renders at 1280 and 390 CSS pixels. The selected direction also renders every state in night mode at both widths. Step-up and Users reset select `a-password`, labelled A2. A1 remains in the lab as superseded history, with light frames only.

- Account shows off, set-up unfinished, on, one recovery code left, regenerate confirmation, turn-off confirmation, a rejected proof and turn-off success. Both confirmations require the current password and an authenticator code or recovery code. Each shows recovery entry, daily throttling and address throttling.
- Enrolment shows the QR, the manual key, confirmation, an incorrect code and expired set-up. Direction A keeps scan and confirmation together. Direction B splits them.
- Recovery shows the initial set, copy feedback, download feedback, saved confirmation and a regenerated set that replaces the old codes.
- Login shows authenticator and recovery entry, each rejected, expiry, the daily authenticator budget and the separate per-address limit. Connected-app variants keep Farm Ledger visible through errors, expiry, both throttles, recovery entry and the return to the password. The long-name state checks app and account wrapping.
- Step-up shows the user-administration form and consent, incorrect proof and rate limiting in each. Both directions show a verification step; A keeps it inline. A1 shows code alone. A2 adds the signed-in user's current password in the same form. I chose password plus code in item 11 of the design record. A2 applies; A1 is not used. Recovery entry retains the password, the new-password draft and consent choices when switching methods.
- Users shows selection, reset confirmation, incorrect proof, rate limiting, reset success and the caller's own row. The self row sends the user to Account / Security instead of offering an administrative self reset. A2 also shows the password-plus-code policy in the reset confirmation, including the recovery-code path.

`TERMS` at the start of the lab script holds the three shared terms. [The design record](https://github.com/mforce/cluckwork/issues/1274) controls the final login and step-up rules. These mockups do not settle lifetimes, rate-limit durations, code count, reset session effects or the later Owner enrolment requirement. Ten generated sample codes and the remaining-code counts illustrate the layout only. The design record in [#1334](https://github.com/mforce/cluckwork/pull/1334) requires 16-character recovery codes. Samples use four groups of four characters separated by hyphens. Recovery fields accept the 16 characters with or without those hyphens. The lab generates deterministic samples in memory from a fixed seed, so every run, width and theme uses the same examples. No code literal is committed.

The night-mode primary button matches the live app. `FarmThemeProvider.tsx` maps `palette.primary.main` to `--brand` and `contrastText` to `--on-brand`. `farmThemeOverrides.ts` leaves contained buttons on that background in night mode. The lab retains those exact tokens, even where the fill looks dark against the card.

Daily authenticator-budget throttling keeps **Use a recovery code** enabled because recovery codes are outside that budget. The separate per-address limit disables the code field, submit action and method switch.

## Existing behaviour to preserve

These groups came from a search of the current test files before the layouts were built. This lab changes none of them. The application slices must retain their guarantees and extend their expectations for an enrolled user as the design record requires.

| Tests found | Design treatment |
| --- | --- |
| `AccountPage.test.tsx`: `uses the Focus-panel width and opens Change password initially`; `changes the password and reports the other devices were signed out`. | Keep the 760px content width and existing password panel. The lab collapses unrelated panels to focus the Security frames. Production must retain the existing initial expansion. |
| `Login.test.tsx`: `uses the flat inset Field Console auth frame`; `returns to the consent route with its request after sign-in`; `names the app it continues to when sign-in leads to consent`; `shows a rate-limit message on a 429 and stays on /login`. | Keep the chosen auth frame, return request and app name throughout the code step. Add code-specific errors without changing the existing password-stage defences. |
| `ConnectPage.test.tsx`: `names the signed-in account prominently, not as a muted caption`; `spends a step-up grant on Allow and sends the browser to the app`; `keeps the choice after a wrong password`; `locks the choice while the password and the approval are checked`. | Keep the prominent email, selected permissions and grant consumption. A1 replaces current-password entry with an authenticator field. Selected A2 retains the password and adds the code, as the design record requires. Both directions keep the same request in view. |
| `UsersPage.test.tsx`: `keeps edit and role in the row and every action in the selected user's inspector`; `sets a user's password from the inspector action, sending id + password + a key`; `keeps the dialog open and shows the error when the server rejects it`; `does not let a dismissed password-reset continuation write`. | Add reset to the inspector. Keep errors in the active dialog and preserve its session boundary. Change only the enrolled caller's proof field in Set password. |
| `UsersPage.test.tsx`: `never stores the entered step-up password: reopening after a successful use shows it empty`; `clears any half-entered step-up password the instant the session ends`. | Retain transient proof entry. Authenticator input must clear on submission, close and logout in the real implementation. |

All test paths above are under `web/src/routes/`. No production test is retired by this design.

## Render and inspect

Run at the repository root:

```sh
node docs/designs/1275-two-factor/shoot.mjs
```

The script uses the local Playwright and Inter installs, then the shared checkout's installs if this worktree has none. `PLAYWRIGHT_DIR`, `INTER_WOFF2` and `CHROMIUM_PATH` override those locations. A direct HTML open without the font install uses the system fallback; the render script embeds the installed Inter font.

Frames go in the ignored `renders/` directory beside the lab. Pass another directory as the first argument if needed. Pass a screen key as the second argument to capture only that screen while keeping every assertion, for example `node docs/designs/1275-two-factor/shoot.mjs docs/designs/1275-two-factor/renders recovery`. The script writes `manifest.json` with the screen, direction, width, state, theme and actual height for each frame. Names follow `<screen>-<direction>-<width>-<state>-<light|dark>.png`.

The output uses `deviceScaleFactor: 1` and screenshot scale `css`. Long pages retain their full height; 390px captures are not shrunk to fit 844px. Dialogs use the live control structure, with a title, scrolling content and a separate right-aligned actions row. The phone captures check that the entire primary action is visible outside the scrolling content. The script checks horizontal frame overflow and exercises saved-code confirmation, enrolment, dialog dismissal, the connected-app recovery switch, the reset flow, recovery input with password retention and the two throttle behaviours. It records no claim about real authentication or clipboard access.

Images stay outside git. The [renders comment](https://github.com/mforce/cluckwork/issues/1275#issuecomment-6295469949) holds the attached frames. This lab is a design instrument, not production UI.

## Passkeys ([#1283](https://github.com/mforce/cluckwork/issues/1283))

The [2026-11-17 screen amendment](https://github.com/mforce/cluckwork/issues/1283#issuecomment-6941756512), the last amendment on [the passkey epic](https://github.com/mforce/cluckwork/issues/320) and the merged **Passkey** glossary entry govern these additions. They do not change the historical picks above. The maintainer picked A for all eight screens on [2026-11-18](https://github.com/mforce/cluckwork/issues/1283#issuecomment-6968264682).

Reading this as security screens for farm staff, preserving the shipped design. Design variance 3, motion intensity 1, visual density 5. The design-taste skill's audit, preservation, responsive and accessibility guidance applies. Its marketing layouts, new packages and decorative imagery do not fit this product lab. The lab keeps its existing HTML control approximations, live token import, Inter body, Georgia titles, 4/8/12px radii, Login card, Account accordion, inline step-up and selected-person inspector. Browser-owned passkey dialogs are described rather than reproduced. The autofill panel is explicitly an illustration; its position, content and appearance vary by browser.

| Screen key | States | Recommendation |
| --- | --- | --- |
| `pk-account` | `empty`, `app-only`, `feature-off`, `one-owner`, `both`, `limit`, `multi-address`, `rename`, `remove`, `remove-code`, `remove-recovery`, `removed`, `last-factor` | A, address under name. Keeps name, date and address together on phone and desktop. B compares a separate desktop address column. Only `multi-address` displays addresses. |
| `pk-registration` | `stepup-password`, `stepup-passkey`, `stepup-passkey-only`, `stepup-code`, `stepup-recovery`, `stepup-app-only`, `stepup-app-only-recovery`, `first`, `later`, `with-app`, `waiting`, `success`, `cancelled`, `unsupported` | A, existing inline proof and set-up panel. Name the passkey before opening the browser dialog. `first` warns about other sessions; `later` and `with-app` do not. |
| `pk-login` | `ready`, `connect`, `cancelled`, `unknown`, `wrong-farm`, `second`, `second-code`, `second-recovery`, `second-passkey-only`, `connect-second`, `connect-second-code`, `connect-second-recovery`, `off`, `off-code`, `off-recovery`, `off-signed-in`, `signed-in`, `connect-return`, `password-change` | A, outlined passkey action below the password form. Preserves the shipped field order. B puts the faster route before the fields. |
| `pk-autofill` | `suggestion` | A, browser suggestion only. B adds helper copy above Email. Both draw the same illustrative suggestion and use `username webauthn`. |
| `pk-stepup` | `dialog`, `dialog-code`, `dialog-recovery`, `dialog-passkey-only`, `consent`, `consent-code`, `consent-recovery`, `consent-passkey-only`, `cancelled`, `wrong`, `done` | A, selected inline layout. A passkey proves identity alone. The app fallback requires password plus authenticator code or recovery code. Consent's combined action checks the passkey before approval. |
| `pk-owner` | `notice`, `setup` | A, existing deadline notice on Account and standalone set-up panel with both methods and Sign out. Exact notice: "Every Owner must set up a passkey or two-factor authentication". |
| `pk-suggestion` | `offer`, `offer-app`, `skipped`, `skipped-app` | A, existing auth card with Create passkey and Not now. One-time offer for eligible users after password sign-in; Owners who see the requirement notice do not see it. |
| `pk-users` | `selected`, `confirm`, `confirm-code`, `confirm-recovery`, `done`, `self` | A, existing inspector and confirmation. Remove passkeys is separate from Reset two-factor authentication. Every target session and connected app ends; their authenticator app and recovery codes remain. |

A passkey can complete sign-in with the farm code blank. The wrong-farm refusal keeps the entered farm code visible. Success shows the passkey's farm remembered on the device. Both the direct passkey route and password second step retain Farm Ledger through authenticator/recovery switches, then return to its permission request. Password sign-in with either factor continues to the second step. The passkey-only state offers no code fallback and never allows the password alone to complete authentication. Passkeys off removes the button and the `webauthn` autocomplete token; its password continuation offers only authenticator or recovery codes. Success and the final Continue keep passkeys unavailable on Account. The forced password-change frame follows passkey sign-in.

The suggestion fixtures cover no enrolled factor (`offer`) and an enrolled authenticator app (`offer-app`). Both assume a supported browser, no existing passkey and no Owner requirement notice. The suggestion appears once; Not now dismisses it. Both skipped fixtures continue to Account with no passkeys and the starting app status. Creating a passkey requires password proof in the no-factor case and password plus authenticator/recovery code in the app case.

Account shows both factors in one accordion. Dialogs retain the starting app enrollment, passkey list and Owner setup context. Rename changes only the chosen name; registration adds one key; cancellation returns to the starting screen without changing enrollment. The same facts survive the shared authenticator-app setup screens, including cancellation and Done after saving recovery codes. An Owner with a single passkey cannot remove it while the requirement is on and no app is set up; the explanation and backup suggestion remain next to that key. A user with passkeys alone gets no recovery codes. Removing a passkey warns about other devices and connected apps, while this device stays signed in. The addresses, names and dates are layout fixtures, never environment configuration or credentials.

### Current behaviour and tests

Searches with `rg -n 'it\(|test\('` in the paths below ground these layouts. These are existing product tests, not assertions that the mockup implements authentication. This docs-only lab retires none of them. Future implementation slices extend the named cases for passkeys.

| Current tests | Preserve or extend |
| --- | --- |
| `web/src/routes/account/Security.test.tsx`: `offers Set up, and nothing else, while two-factor is off`; `shows the codes left with Regenerate, Replace and Turn off once it is on`; `steps up with the password, shows the QR code, the key and the phone link, and warns before confirm` | Keep app management and its enrolment flow together. Add the passkey list in the same accordion. Extend proof to accept a passkey alone and keep password plus code for the app. |
| Same file: `is not offered while the status route sends requiredBy, and says why`; `shows the Owner refusal, not a generic error, when the screen was stale` | Keep the visible explanation. Apply it to removal of the last factor; the other factor permits removing one. |
| `web/src/routes/TwoFactorDeadlineBanner.test.tsx`: `shows the deadline in the user's language`; `web/src/routes/TwoFactorEnrolmentPage.test.tsx`: `replaces the screen a 403 came from, focuses its heading, and fires only requests the gate allows` | Keep the deadline notice before the gate, standalone setup after it, and Sign out. Add passkey as the other allowed method. |
| `web/src/routes/UsersPage.resetTwoFactor.test.tsx`: `describes the confirmation by its target, then what the reset ends`; `names the target and the signed-in Owner who confirms the reset` | Preserve both identities and the explicit session warning for the separate passkey-removal action. |
| `web/src/routes/Login.test.tsx`: `uses the flat inset Field Console auth frame`; `returns to the consent route with its request after sign-in`; `names the app it continues to when sign-in leads to consent`; `shows a rate-limit message on a 429 and stays on /login` | Keep frame, return request, app context and existing defences. Extend successful sign-in and the second step for passkeys; permit blank farm code only on the passkey path. |
| `web/src/routes/ConnectPage.test.tsx`: `spends a step-up grant on Allow and sends the browser to the app`; `ticks every permission the role can use on a first request`; `keeps the longer explanations and the sign-out under Details` | Keep choices and grant use. A passkey changes the proof method; it does not bypass consent. Existing legal and consent copy stays in the implementation. |
| `web/src/routes/UsersPage.test.tsx`: `keeps edit and role in the row and every action in the selected user's inspector`; `never stores the entered step-up password: reopening after a successful use shows it empty`; `clears any half-entered step-up password the instant the session ends` | Keep the inspector and transient proof lifecycle. Add a separate Remove passkeys action; never fold it into reset two-factor. |

### Render passkeys

```sh
node docs/designs/1275-two-factor/shoot.mjs /home/mforce/.cluckwork-slices/1283-renders passkeys
```

Without the final screen argument the script also captures all historical screens. `passkeys` captures all `pk-` screen keys. Every new state renders at 1280 and 390 CSS pixels, with day and night captures for recommended A and day captures for B. The script still checks all frames and exercises the historical interactions when a capture filter is present. It discovers every Continue, Done and Cancel action across the passkey fixtures and walks each through its final screen at both widths. It also completes or cancels registration from each Account, Owner and suggestion origin and checks shared authenticator-app setup exits. The output `exit-walk.json` lists each source, action and destination. Its additional checks cover empty farm-code passkey sign-in, `/connect` return through password and method switches, feature-off entry and continuation, suggestion proof for both enrollment fixtures, registration and rename return facts, cancelled Owner setup, dialog focus and Escape dismissal, five-key limit, last-factor explanation, address visibility, creation warnings, password/code/recovery switches, retained drafts and consent choices, admin removal and Not now.

Frames use `<screen>-<direction>-<width>-<state>-<light|dark>.png`; `manifest.json` lists every captured file and height. Phone code and recovery dialogs capture the content scrolled to the method switches; the fixed title and primary action stay visible. The assigned render directory is outside git. No images belong on this branch. Authentication, platform WebAuthn, browser support detection, persistence and session effects are simulated; the render checks establish prototype behaviour only. The [attached frames](https://github.com/mforce/cluckwork/issues/1283#issuecomment-6967543476) are available for reference.
