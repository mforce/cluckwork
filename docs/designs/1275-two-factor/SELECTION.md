# Two-factor authentication screens: selected directions

On 2026-10-21, the maintainer answered "ok A both then" to "Enrolment A or B, Step-up A or B". The coordinator relayed that answer and asked to record the four single-design screens too. Those four had no competing direction, and the coordinator reported no objection to them.

The table records the picks for [#1275](https://github.com/mforce/cluckwork/issues/1275). The reasons describe the selected layouts, not additional quotations from the maintainer. The [renders comment](https://github.com/mforce/cluckwork/issues/1275#issuecomment-6295469949) shows the comparison frames.

| Screen | Pick | Representative frame names | Reason |
| --- | --- | --- | --- |
| Account | One design, Security accordion | `account-a-1280-on-light.png`, `account-a-390-on-light.png` | Keeps the two-factor status, remaining recovery codes, Regenerate and Turn off together. |
| Enrolment | A, scan and confirm together | `enrolment-a-1280-scan-light.png`, `enrolment-a-390-scan-light.png` | Keeps the QR, manual key and confirmation on one page while the user switches to an authenticator app. |
| Recovery codes | One design, save before leaving | `recovery-a-1280-new-light.png`, `recovery-a-390-new-light.png` | Keeps the once-only list, copy and download actions, and saved confirmation together. |
| Login code | One design, existing Login B card | `login-a-1280-code-light.png`, `login-a-390-connect-light.png` | Keeps the farm, account and connected app visible during the code step. |
| Step-up | A, inline password plus code, labelled A2 in the lab | `stepup-a-password-1280-dialog-light.png`, `stepup-a-password-390-dialog-light.png`, `stepup-a-password-1280-consent-light.png`, `stepup-a-password-390-consent-light.png` | Keeps the action, target and identity proof in the same form. |
| Users reset | One design, selected-user inspector and confirmation | `users-a-1280-selected-light.png`, `users-a-390-selected-light.png`, `users-a-password-1280-confirm-light.png`, `users-a-password-390-confirm-light.png` | Keeps reset beside the person it affects and identifies the signed-in account that confirms it. |

Frame names refer to the ignored `renders/` directory beside this file. `node docs/designs/1275-two-factor/shoot.mjs` regenerates them at 1:1. No images are committed.

## Step-up uses the password and a code

The maintainer decided on 2026-10-20 that an enrolled user steps up with the password and a code. Item 11 of [#1274](https://github.com/mforce/cluckwork/issues/1274), recorded in [#1334](https://github.com/mforce/cluckwork/pull/1334), owns that rule. The 2026-10-21 pick chooses layout A, not a different proof policy.

The selected frames use `a-password`, labelled A2 in the lab. A1 "code alone" is not used. The Users A1 frames in the renders comment show the inspector and the layout only. The Users A2 confirmation supplies the applicable password-plus-code proof.

The lab retains the discarded B layouts and A1 proof variants as exploration history. They do not override these selections. A2 frames are currently light only. The implementation must verify password-plus-code forms in night mode too.

## Copy terms

The user-facing terms are "two-factor authentication", "authenticator app" and "recovery code". The lab's `TERMS` object keeps those terms together.
