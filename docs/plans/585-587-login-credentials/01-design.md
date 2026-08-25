# #587: revocable remembered farms, with stable login-field identifiers

## Goal

Let an operator remove one remembered farm from the device-local login roster
without clearing unrelated browser preferences. Give the existing farm-code,
email, and password controls stable HTML `id` and `name` values. Do not change
the login request shape or the username/password autocomplete semantics.

## Scope and ownership

| Slice | Ownership and ordering |
| --- | --- |
| #535 (shipped in `b7f5cc6c`) | Owns the existing URL/cache prefill and valid-code-only roster. This slice extends its cache and picker; it does not change its prefill precedence. |
| #587 (this slice) | Owns removal from the remembered-farm roster, its confirmation flow, and the stable field identifiers transferred into this slice by the owner. |
| #585 | Deliberate won't-fix for farm-qualified password-manager credentials. This PR ships the separately useful stable identifiers under #587, amends epic #530 with the deliberate decision, then amends and closes #585 without claiming its unmet manager-behavior criterion. |
| #537 | Owns the multi-farm ADR and repository-wide docs sync. This slice updates the immediately user-visible Help/glossary copy, but defers the ADR's accepted-disclosure revision to #537. |

## Conflict table

| Requirement | Repository rule / shipped behavior | Decision |
| --- | --- | --- |
| #587 requested a #537 ADR update | #537 is the dedicated ADR/docs slice and no ADR file is in this branch | Amend #587 to defer that acceptance item to #537. |
| #585 proposed password-manager differentiation | HTML supplies no standard manager storage-key contract; user chose no username/password change | Close #585 as a deliberate won't-fix. |
| Forgetting the last code must work | #535 hides the picker when exactly one code is remembered | Render the picker for one or more remembered codes. |

## Design

`farmCodeCache.ts` remains the deep module for roster storage. Add
`removeFarmCode(value): Promise<void>` beside `rememberFarmCode`. It
canonicalizes its argument, re-reads the roster inside the existing Web Locks
critical section, removes only the matching canonical code, and writes the
remaining raw JSON array. It has the same best-effort contract as
`rememberFarmCode`: invalid input, unavailable storage, or a rejected lock
never rejects a completed sign-in flow or the login screen. A successfully
acquired Web Lock orders a forget with a successful sign-in; the existing
no-lock/rejected-lock fallback is deliberately an unsynchronised,
best-effort read-modify-write and makes no cross-tab ordering promise.

`Login` keeps remembered codes in mutable component state. The picker renders
for one or more valid remembered codes, except when a valid `?farm=` value
supplies the farm (the existing source-precedence rule). Each entry has two
separate controls: selecting the code fills the field; an explicit, accessible
Forget control opens the shared `useConfirm` destructive dialog. Confirming
optimistically removes that code from page state and clears the farm-code input
when it held that code; canceling makes no change. The confirmed handler queues
focus to the surviving `#farm-code` input after the state commit, because the
triggering Forget button is removed and `Dialog` rightly declines to restore
focus to a disconnected trigger. With no remaining entries, the picker is
absent. A later successful sign-in may deliberately remember the farm again.

The three existing controls get stable identifiers only:

| Control | `id` | `name` | Existing autocomplete |
| --- | --- | --- | --- |
| Farm code | `farm-code` | `farmCode` | unchanged (no standard farm-code token) |
| Email | `email` | `email` | `username` |
| Password | `current-password` | `password` | `current-password` |

The picker style changes from a single button per code to a compact entry with
a clearly distinct selection button and a 44px minimum touch-target Forget
button. A stylesheet structural test pins that minimum in every matching
Forget selector, since jsdom does not perform layout. The shared confirmation
dialog makes removal deliberate; the confirmed handler returns focus to the
farm-code input. Auth-copy confirmation strings ship in `en`, `es`, and `tl`;
`useConfirm` already supplies the translated Cancel button.

## Invariants and enforcement sites

| Invariant | Enforcement sites |
| --- | --- |
| Only canonical, valid farm codes reach the picker or are removed | `canonicalFarmCode`, `readFarmCodes`, `removeFarmCode`, cache and login tests |
| A successful Web Lock prevents independent tabs dropping an unrelated roster entry through a normal write race | `ROSTER_LOCK`, both cache writers, structural Web Locks tests; no-lock/rejected-lock remains best-effort |
| URL prefill does not expose/cache-pick another farm | `urlFarmCode` gate in `Login`, existing prefill tests |
| Forgetting is deliberate and removes only its selected code | `useConfirm`, `Login` state update, raw-storage and UI tests |
| A Forget control remains usable by touch | `styles.css`, a selector-aware stylesheet structural test |
| Confirming a removed entry never leaves focus on `<body>` | Login confirmed-removal focus test; queued focus to `#farm-code` |
| User-visible behavior is documented in all supported locales | `specs/product/GLOSSARY.md`; Help and glossary entries in `en.ts`, `es.ts`, and `tl.ts` |

## Verification shape

The implementer writes the tests red first. Cache tests assert raw
`cluckwork.farmCodes` JSON after removing one of several and the sole code,
and assert best-effort behavior when storage throws. Login tests cover one-code
picker rendering, cancel, confirmed removal, clearing the selected input,
disappearance after the last code, focus on the farm-code input after a
confirmed removal, and all three stable `id`/`name` pairs. The cancellation
test asserts the raw cache and visible picker are unchanged. A selector-aware
stylesheet test proves every matching Forget control has a `min-block-size` or
`min-height` of at least `44px`, including media-query overrides. Existing
URL-precedence tests remain unchanged.

Mutation checks for Phase 11 are delegated to a non-implementer because the
owner chose zero driver edits including transient verification changes:

- remove the `filter((c) => c !== code)` predicate and run the named raw-storage
  removal test (it must fail at the JSON assertion);
- remove the `farmCode === code` clear branch and run the named Login test (it
  must fail because the field still contains the forgotten code);
- bypass the `await confirm(...)` gate and run the named cancellation test (it
  must fail because the roster changes after one Forget click);
- remove the 44px minimum declaration and run the named stylesheet test (it
  must fail on the effective Forget target);
- remove each control's `id` or `name` attribute in turn and run the named
  identifier test (it must fail on that exact attribute).

No server, API contract, migration, or Playwright fixture changes in this
slice. The API request body and the existing username/password `autocomplete`
tokens stay unchanged; IDs and names may influence browser/password-manager
heuristics, so no browser credential behavior is promised.
