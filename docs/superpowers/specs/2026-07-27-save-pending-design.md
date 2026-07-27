# Save pending states (#236) — design (v3)

v1 was reviewed by three reviewers (codex: REDESIGN, pi + architect:
APPROVE-WITH-CHANGES); v2 folded in the accepted amendments; a codex re-pass
on v2 resolved four of its findings and corrected the rest — v3 folds those
in. The appendix records what was rejected and why.

## Problem

Every mutating action disables its trigger while in flight, but the only
visual is `button:disabled { opacity: .5 }`. A save that takes seconds — a
lock wait behind a currency change (#162), a cold DB, bad rural connectivity —
is indistinguishable from a dead button. Mechanisms are hand-rolled three
ways (`busy` useState, scoped `run()` helpers, Settings' `saving`+`logoBusy`),
one form today has NO guard at all (DailyEntryPage `onCreateFlock`), and the
next screen re-rolls the pattern again.

## Decision

Two shared pieces; the risky per-screen logic (idempotency keys,
refresh-before-rotate) stays where it was already reviewed.

### 1. `usePendingAction()` — `web/src/components/usePendingAction.ts`

("save" was wrong for login/void/archive/assign. Placement per `useConfirm`
precedent: domain-agnostic UI hook → `components/`.)

```ts
export function usePendingAction(): {
  busy: boolean;                      // any flight open
  isPending(scope: string): boolean;  // exactly this one (per-row spinners)
  run<T>(scope: string, action: () => Promise<T>): Promise<T | undefined>;
}
```

- **Synchronous re-entry guard by ref**: an internal `inFlightRef` is set
  before `action` is invoked and cleared in `finally`. React state cannot
  guard two calls in the same tick; the ref can. Unit-tested with two
  synchronous `run()` calls — the action must run once.
- `run` returns `undefined` when skipped. Callers MUST NOT read `undefined`
  as success; screens that branch on success keep their own wrappers (the
  `run()` helpers already return booleans).
- Exceptions propagate; the flight closes in `finally`. Error rendering
  stays per screen.
- `isPending(scope)` is the single source of truth for per-row spinners —
  no scope-string reconstruction at call sites beyond the one literal.
- Scope taxonomy: any stable string — `"create"`, `"archive:<id>"`,
  `"void-payment:<id>"`, composite where the action is payload-bound
  (`"assign:<user>:<flock>"`, `"adjust:<item>:<lot>"`). Pending scopes are
  INDEPENDENT of idempotency-key scopes: the `run()` screens already build
  composite key scopes, and those strings carry over unchanged as pending
  scopes too — but nothing couples the two. Sales' helper also wraps two
  READS (open ledger, load more); they keep the guard and get scopes
  (`"open:<id>"`, `"more"`) but no BusyButton treatment — #236 is writes.
- **Not owned by the hook**: idempotency keys and refresh-then-rotate. The
  `run()` screens rebase their helpers on `usePendingAction().run` (helper
  gains/keeps its scope parameter — Sales' scopeless helper gains one),
  dropping only hand-rolled busy bookkeeping.
- Unmount mid-flight: the `finally` setState after unmount is a silent no-op
  on React 18+ and identical exposure to all 18 current hand-rolled
  implementations — explicitly out of scope, no mounted-ref.

**Why not framework primitives** (standing prefer-framework rule):
`useActionState` expects `<form action={fn}>`; this repo deliberately uses
`onSubmit` handlers and some dialogs are deliberately not forms, so adopting
it means a form rearchitecture against documented decisions. `useTransition`'s
`isPending` offers no scopes and nothing for the key/refresh composition.
Both evaluated, both wrong-shaped; recorded here so the hook doesn't read as
an oversight.

### 2. `<BusyButton>` — `web/src/components/BusyButton.tsx`

```tsx
type Props = React.ButtonHTMLAttributes<HTMLButtonElement> & { busy?: boolean };
```

Renders (fragment):

- the `<button>`: `disabled={disabled || busy}`, `aria-busy` while busy,
  children passed through untouched — **dynamic children are the caller's**
  (Login keeps its `{busy ? "Signing in…" : "Sign in"}` swap and its
  exact-accessible-name test);
- while busy: spinner overlaid centered, `aria-hidden="true"` (must not
  contribute to the accessible name), label dimmed via a SINGLE opacity
  layer;
- a visually-hidden `role="status"` live region as a SIBLING of the button
  (inside the fragment, `position: absolute` so no layout effect),
  **always mounted** with its text swapped between empty and the status
  string — a region that mounts already populated is unreliably announced;
  the repo already uses this exact pattern (SettingsPage logo status). Text
  from a new i18n key (`common:working` → "Working…", the wording
  SettingsPage already uses — "Saving…" would be wrong for
  sign-in/void/archive/assign). Sibling, not child: `aria-busy` tells AT to
  DEFER announcing changes inside the busy element, so a live region inside
  the button may never speak. This is the actual screen-reader signal;
  `aria-busy` itself is metadata.

CSS (`styles.css`):

- **`button:disabled[aria-busy="true"] { opacity: 1; }`** — without this the
  global `:disabled { opacity: .5 }` halves the whole subtree, spinner
  included, and the busy state reproduces the exact dead-button look this
  issue exists to remove (design-review blocker).
- `.busy-label { opacity: .45 }` (single layer — never stacked on the .5).
- `.spinner`: border ring in `currentColor`, `@keyframes spin`. No new
  reduced-motion query: the existing global reduced-motion rule
  (styles.css:1792) already kills all animation; static ring + dimmed label
  still read as busy.
- Children wrapper stays `inline-flex` so existing icon/text gaps survive.

### Carve-outs (not buttons — BusyButton cannot wrap them)

- **Logo upload** (Settings): `<label class="logo-file"><input type="file"/></label>`
  keeps `disabled={busy}` + its EXISTING `role="status"` region ("Working…").
  Only the Remove button becomes a BusyButton (`logo:remove`).
- **LanguageSelector** (`web/src/session/LanguageSelector.tsx`,
  `PUT /me/language`, a `<select>`): today FIRE-AND-FORGET with no guard at
  all. Gets its OWN component-local `usePendingAction` (single flight is
  per-component here, not screen-wide) and disables while in flight; no
  spinner.
- **Sign-out** (AppLayout): excluded. Best-effort revoke that clears the
  token synchronously and navigates; a spinner there is noise. Recorded as
  a decision.

### Confirm-dialog model (explicit decision)

`useConfirm` settles before the caller's request starts (the dialog's
removal is an enqueued state update — "settled first" is the contract, not
a synchronous unmount) — dialog buttons never OWN in-flight I/O and get no
busy state. The handoff model, specified as OBSERVABLE STATE and tested with
async queries rather than commit-order assumptions: once the confirmed
request is in flight the dialog is gone and the ORIGINATING row control is
the pending indicator (`isPending("archive:<id>")`); on settle no pending
scope remains.

Dialog-closing saves (SalesPage closes inside the callback, FlocksPage after
`await run`) rely on React automatic batching to land close +
busy-clear in one commit; pinned by a test (no stale pending scope, no act
warnings) rather than left emergent.

## Migration matrix

14 mutation-bearing screens, ~52 rendered triggers (v1's "35/12" was wrong;
the implementer AUDITS each screen against this table and flags drift):

| Screen | Actions (scopes) | Today | Work |
|---|---|---|---|
| Login | signin | `busy` | swap to hook + BusyButton |
| AccountPage | change-password | `busy` | swap |
| CustomersPage | create | `busy` | swap |
| DailyEntryPage | save, submit, create-flock | `busy` + ref; **create-flock UNGUARDED** | swap; **new guard** |
| ExpensesPage | create, adjust:id, category-create, category-toggle:id | `run()` | rebase helper (audit: NO void/confirm exists here) |
| FlocksPage | create, update:id, archive:id, deplete:id, reactivate:id, movement | `run()` | rebase helper |
| GradesPage | create, update:id (+de/reactivate) | `run()` | rebase helper |
| HistoryPage | adjust:id, void:id | `busy` | swap |
| InventoryPage | create, update:id, purchase, usage, adjust (+row verbs) | `run()` | rebase helper |
| ProductsPage | create, update:id, conversion | `run()` | rebase helper |
| SalesPage | create-order, add-item, update-item:id, remove-item:id, confirm:id, cancel:id, void:id, record-payment, void-payment:id | scopeless `run()` | rebase helper **+ scope param** |
| SettingsPage | settings, logo:upload (carve-out), logo:remove | `saving`+`logoBusy` (already mutually exclusive via cross-checks) | consolidate on one hook; palette radios + logo status + `focusUploadAfterRemove` re-keyed to SCOPE, not global busy |
| UsersPage | create, update:id, set-password:id, assign:id, unassign:id | `busy` | swap |
| WaterPage | record/update | `busy` | swap |

Every trigger button becomes `<BusyButton busy={…}>`; per-row buttons bind
`busy={isPending("verb:" + id)}` and additionally `disabled={busy}` so the
whole screen stays inert while exactly one control spins.

## Tests

- Hook unit: busy lifecycle; **two same-tick `run()` calls → action runs
  once**; skip returns `undefined`; exception closes flight; `isPending`
  set/cleared; next run allowed after settle.
- BusyButton unit: idle children clickable; busy → `disabled`, `aria-busy`,
  spinner present and `aria-hidden`, **exact accessible name unchanged**;
  live region appears with the i18n text; `busy`+caller `disabled` compose.
- Screen-level (held/deferred promises, no timing guesses):
  - Login (busy convention) — existing test keeps passing (dynamic label).
  - CustomersPage — held first request + second click → ONE api call
    (double-submit under flight, not just after settle).
  - FlocksPage — confirm-dialog handoff: after confirm, the row's Archive
    button spins, sibling rows disabled without spinners.
  - SalesPage — two verbs on one row: void-payment:id spins, remove-item:id
    disabled-not-spinning.
  - SettingsPage — logo upload busy: settings Save disabled but NOT spinning
    (scope isolation), `focusUploadAfterRemove` still fires.
  - Dialog-close success → no stale pending scope, no act warnings.
  - A boolean-returning wrapper (FlocksPage shape) maps a SKIPPED run
    (`undefined`) to `false` — a skip must never close a dialog or reset a
    form as if it succeeded.
  - After a long confirmed request settles, the originating button is
    re-enabled and present; where focus sits during the disabled window is
    explicitly unasserted (appendix — accepted limitation).
- Existing 838 stay green; coverage floors re-baselined both directions.

## Docs

Help page: one line — a spinning button means the save is still working;
pressing again won't double-record. GLOSSARY untouched (no new domain term).
New i18n key `common:working` in every locale file present.

## Appendix — rejected review suggestions (with reasons)

- **Mounted-ref / AbortSignal for unmount** (pi): React 18+ removed the
  warning; setState post-unmount is a no-op; parity with all current code.
- **`aria-disabled` + pointer-events instead of `disabled`** (pi): breaks
  implicit form-submit semantics and every existing `toBeDisabled()`
  assertion; the sibling live region already carries the announcement.
- **`console.warn` on skipped run** (pi): pollutes pristine test output.
- **Focus restoration for disabled rows** (pi): ACCEPTED LIMITATION, this
  paragraph being the record — Dialog's restore retries once on the next
  frame, so a button still disabled seconds later leaves focus at the
  browser default. Multi-second focus parking is out of proportion; the
  live region announces state regardless of focus.
- **Discriminated `{started}` result from `run`** (codex option B): the only
  callers that branch on outcome already wrap with boolean-returning
  helpers, and those wrappers MUST map `undefined` (skip) to `false`
  explicitly — pinned by a test. Documented undefined-on-skip chosen
  (codex option A).
- **`useActionState`/`useTransition`** (architect asked): see Decision §1.
