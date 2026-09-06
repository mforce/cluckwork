# Handoff — #703: shared dialog-session guard for the SPA

Follow-up to #477 / PR #702. Read this before planning; it corrects two things
the issue body got wrong by counting rather than measuring.

Measured on `main` at the time of writing. Re-run the commands in
[§3](#3-measured-state-of-the-code) before trusting any number here.

---

## 1. The defect in one paragraph

A dialog write is in flight. The user dismisses the dialog and reopens it, and
starts typing. The first write lands and runs its success path unconditionally:
it resets the fields the user has just typed into and closes the dialog they are
typing in. Their input is gone.

`useDialogErrors` (#479) already stops an abandoned attempt **reporting a
failure**. Nothing covered the **success** direction, which is worse — it
destroys work rather than withholding a message.

`usePendingAction` does *not* prevent this. Its in-flight ref blocks a second
*submit* while the first is out; it does nothing about the user typing into the
reopened form, which is the whole bug.

PR #702 fixed Sales with `web/src/components/useDialogSession.ts` — a per-scope
monotonic counter, claimed before the first `await`, checked before touching
anything the on-screen session owns.

## 2. The one hard rule — the superseded-safe question

**Ask it per statement in the success path, not once per action:**

> This write succeeded, but the dialog session that started it is gone.
> Must **this particular statement** still run?

Decide by what the statement is *about*:

| The statement is about… | Verdict | Because |
|---|---|---|
| A fact about the world | **must still run** | The write happened; nobody watching does not undo it |
| State the on-screen session owns | **must not run** | That state belongs to a different session |

Worked out on real lines:

| Statement | Verdict |
|---|---|
| `clearKey(scope)` / idempotency-key rotation | **must run** — skip it and the next attempt reuses a spent key, so the server replays the abandoned write and the new one never happens |
| List refresh (`setFlocks(await fetchFlocks())`, `refresh()`, `listUsers()`) | **must run** — the record exists and must be visible |
| A money or irreversible-action confirmation | **must run** — silence makes the user believe it did not happen, and paying twice is the likely next act |
| A plain "Saved" on a create | **must not** — it belongs to the session that asked |
| Form field resets | **must not** — the fields hold what the user is typing now |
| Dialog close (`setCreating(false)`, `closeEdit()`) | **must not** — it closes the replacement |
| Panel/record swap (`setActive(...)`) | **must not** — it hijacks the record the current session is about |

**Both directions are live defects.** Gating too much stranded a spent
idempotency key in #702 (a P1 found by codex). Gating too little is the original
bug. Two of the five defects found in #702 were in this judgement, not in the
gating mechanism.

**A shared hook cannot answer this question.** It supplies `current()`. Where the
check goes is a per-statement decision, ~24 times. That is the argument for
splitting the work, and the reason a mechanical sweep is the wrong shape.

### The ordering that must survive

`FlocksPage`, `GradesPage` and `ProductsPage` run the refresh **before**
`clearKey(scope)`, deliberately: if the refresh throws, the key survives and a
retry replays the idempotent write instead of duplicating it.
`CustomersPage.onSaveEdit` and `UsersPage.onUpdate` do the **opposite** — clear
the key the instant the write is confirmed, before the refresh (#163 review) —
because a *changed* retry after a failed refresh must not be answered from the
cached response.

Both orderings are correct for their screen and neither is this ticket's to
change. Preserve whichever the screen has, and gate neither line.

## 3. Measured state of the code

```bash
cd web/src/routes
for f in *Page.tsx; do
  printf '%-20s modals=%-3s actions=%-3s guards=%s\n' "$f" \
    "$(grep -c '<Modal\|<Dialog\|role="dialog"' $f)" \
    "$(grep -c 'errors.beginAttempt' $f)" \
    "$(grep -c 'const isCurrentDialog' $f)"
done
```

| Screen | Dialogs | Gated actions | Success-path guard today | Classification |
|---|---:|---:|---|---|
| **Sales** | 4 | 1 | `useDialogSession` | **done** (#702) |
| **Users** | 14 | 8 | 6 hand-rolled `{id, generation}` refs + `isCurrentDialog()`; **2 actions use a weaker id-only check** | **mostly correct** — consolidate, and fix the 2 |
| **Customers** | 4 | 2 | `edit-customer` has a generation guard (#625); **`create` has none** | **split** — 1 correct, 1 broken |
| **Daily Entry** | 2 | 1 | none | **broken** |
| **Flocks** | 6 | 1 | none | **broken** |
| **Grades** | 4 | 1 | none | **broken** |
| **Products** | 6 | 3 | none | **broken** |
| **Inventory** | 8 | 3 | none | **broken** |
| **Expenses** | 4 | 2 | none | **broken** |
| **History** | 2 | 2 | none | **broken** |
| **Stock** | 2 | 1 | none for the dialog (it *does* ticket its reads via `lotsReq`/`ledgerReq`) | **broken** |
| Feed | 0 | 0 | — | **not affected** — inline form, no dialog session to supersede |
| Water | 0 | 0 | — | **not affected** |

### Two corrections to the issue body

1. **The issue's step 2 screen list is #479's list copied forward, not a
   survey.** Five more screens (Inventory, Expenses, History, Stock, and most of
   Users) have dialogs and gated actions.

2. **"Users is the biggest gap" — filed in the first amendment — is wrong.**
   Users has the *most actions*, but it already implements this pattern by hand
   in six places and already answers the superseded-safe question correctly
   (`clearKey` before refresh, refresh unconditional, message and close gated).
   Users is a **consolidation**, not a fix — with two genuine exceptions below.

### The two real Users defects

`onSubmitStepUp` (disable/enable, `UsersPage.tsx:685`) and `onUpdate`
(edit-user, `UsersPage.tsx:735`) guard on **identity, not generation**:

```js
if (activeStepUp.current !== target.id) return;   // dialog moved on
if (activeEdit.current !== target.id) return;
```

Close and reopen the **same** user and the id matches, so the guard passes and
the stale success closes the reopened dialog. A generation distinguishes the two
sessions; an id cannot. Same defect class as #625, which is why Customers' edit
path already carries a generation.

## 4. Recommended step plan

Supersedes the numbering in the issue body.

| PR | Scope | Actions | Notes |
|---|---|---:|---|
| **1** | Extract the shared hook; migrate **Sales** onto it | 1 | Behaviour-neutral. Proves the shape against the only screen known correct. Sales' existing #702 tests must pass unchanged. |
| **2** | **Customers, Daily Entry, Flocks, Grades, Products** | 8 | Flocks/Grades/Products already have a `run(scope, errorScope, action)` wrapper — near-mechanical. Most of the diff is Customers' `create` and Daily Entry. Customers' `edit` becomes a *deletion* of the hand-rolled generation. |
| **3** | **Inventory, Expenses, History, Stock** | 8 | No existing guards; each needs the superseded-safe question answered from scratch. |
| **4** | **Users** | 8 | Consolidation onto the shared hook, replacing six hand-rolled generation refs. **Carries the two real fixes** (`onSubmitStepUp`, `onUpdate` id→generation). Its own PR: set-password, change-role, change-email and disable/enable are where a wrong gate decision is expensive to detect. |
| **5** | Non-dialog / panel callers | ~4 | Sales panel actions (add-line, update, remove, confirm, void). A panel is not a dialog session; the right semantics differ, so decide them here rather than assuming. |
| **6** | Finding 3 — `useDialogSession` fails open on a mistyped scope | — | Decide once the hook has more than one consumer. Today `begin("create")` + `claim("create-order")` never invalidates. It is a deliberate default (an unadopted screen behaves exactly as before, rather than silently gating every success off), and it becomes a sharp edge at scale. |

Do not merge 2, 3 and 4 into one PR. 24 gate decisions in one diff is enough to
hide the class of defect this whole ticket exists to remove.

## 5. The hook to extract

Source: `web/src/routes/SalesPage.tsx`, the `run` wrapper (search for
`const run = (scope: string, fn: (current: () => boolean) => Promise<void>)`).
It already composes the three existing pieces:

- `usePendingAction.run(scope, …)` — the in-flight guard (#236)
- `useDialogErrors.beginAttempt` / `report` — failure routing (#479)
- `useDialogSession.claim` / `isCurrent` — the success gate (#702)

Shape to preserve exactly:

1. `beginAttempt(slot)` **inside** the wrapper, so a skipped run does not blank
   the message the previous attempt left.
2. `claim(slot)` **before anything awaits** — it must name the session the user
   was in when they asked, not whichever is current when the network answers.
3. `current()` handed to the action, so a **non-dialog** scope is always
   current rather than being gated by accident.
4. `report(slot, …)` in `catch`, which owns the abandoned-attempt decision.

Screens must end the session at **both** dialog open **and** dismiss.

> **Amended 2026-09-06, after PR 1 (#704) shipped the hook.** The sentence above
> originally read "call `session.begin(scope)` at both edges". The shipped hook
> owns its own session and error instances, so a screen never calls `session.begin`
> or `errors.abandon` itself: it calls **`openDialog(scope)`** when the dialog opens
> and **`dismissDialog(scope)`** when it is dismissed or force-closed. Both names
> resolve to one body that **mutes the attempt still out and then ends the
> session** — review round 1 of #704 found that doing only one of the two on one
> edge let a stale failure land in the replacement dialog (invariant INV-7 on the
> PR). A screen that calls `session.begin` directly would update a different
> session map and skip the mute.

## 6. Traps

Each of these has already cost a review round somewhere in the chain
`#474 → #477 → #479 → #625 → #702`.

1. **`clearKey` is not a UI statement.** Gating it strands a spent idempotency
   key: the next attempt reuses it, the server replays the abandoned write, and
   the write the user actually asked for never happens. P1 in #702.
2. **Re-check after *every* await, including the second one.** #702 shipped a
   guard after the POST and none after the follow-up GET; three reviewers found
   it independently. If the action awaits twice, check twice.
3. **Check in `catch` too.** A superseded failure must not report against the
   replacement session — that is #479's half, and it is easy to drop while
   restructuring the `try`.
4. **A skipped `run` returns `undefined`, never success.**
   `FlocksPage.tsx:139` maps it to `false` for exactly this reason; a caller
   that treats `undefined` as truthy closes a dialog that never saved.
5. **Reset the whole form, including defaults.** #702 found the pay-method
   select was never reset because it had a non-empty default. Name the default
   once and reset to it alongside the text fields.
6. **Do not add a second generation beside an existing one.** Customers' edit
   path and Users' six paths already have one; replace, do not layer.

## 7. Verification, per migrated screen

- **The behaviour test.** Submit → dismiss → reopen → type → land the original
  success. Assert the replacement session's input **survives** and its dialog
  **stays open**.
- **The other direction.** A separate test asserting `clearKey` / key rotation
  and the list refresh **still fire** when superseded. This is the direction
  that silently loses money or orders, and no reviewer will notice its absence.
- **Mutation-check every gate.** Remove the gate → that screen's named test must
  go red, *on the assertion the mutant names* (a suite red proves another test
  killed it). A survived mutant means the assertion is vacuous — #702 shipped
  two vacuous assertions found exactly this way.
- **Baseline first.** A mutation run where the unmutated case also fails proves
  nothing. Print baseline, mutants, and what must still pass.
- Tests are Vitest in `web/`; follow `web/src/routes/SalesPage.test.tsx` and
  `web/src/components/useDialogSession.test.ts`.

## 8. Out of scope

- **A `<FormDialog>` component** owning open/close/session/errors would close
  more of the gap by construction, but it rewrites every dialog's markup.
  Recorded as an alternative on #703; not proposed.
- **Feed and Water.** Inline forms, zero dialogs. `runWrite` already covers
  their list side.
- **Changing the refresh/`clearKey` ordering** on any screen (see §2).
