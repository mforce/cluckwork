# Runbook — list-screen layout and empty states (#653, #655)

You are an autonomous coding agent with full tools in a git worktree of the Cluckwork repo
(React 19 + Vite SPA in `web/`). Execute this top to bottom. You do everything except the merge.

**Two issues, one slice, because they edit the same screens.** Read both first:
`gh issue view 653` and `gh issue view 655`.

- **#653** — provenance column to one line; date-range filters into a bounded toolbar; reference cells stop wrapping.
- **#655** — one `EmptyState` component: icon, one sentence, the primary action; role-aware.

**This runbook is dispatched in two runs.** Increments 1–3 are #653. Increments 4–6 are #655. If you are
reading this in the first run, stop after increment 3's commit and report; the driver dispatches the rest.

## Rules

- Transcribe blocks marked **PROTECTED** verbatim; do not improve them.
- **Do not touch:** `web/src/styles.css`'s `.toolbar` rule (it is already styled and carries a comment
  saying so — you are adding *consumers*, not restyling it), `web/vite.config.ts`, `AGENTS.md`,
  `docs/decisions/**`, anything under `src/` (the .NET backend).
- **Files you may create or edit:** `web/src/components/**`, `web/src/routes/**`, `web/src/lib/**`,
  `web/src/i18n/{en,es,tl}.ts`, `web/src/styles.css` (additions only, for `EmptyState` and the
  reference-cell nowrap), `tools/simulation/ui/specs/**` if a caller genuinely breaks, and this plan
  directory. Anything else: STOP and report.
- **Every `web/` change ships Vitest tests in the same PR.** That is a repo rule, not a preference.
- **i18n is not English-first here.** Every new string ships `en`, `es` and `tl` inline in the same
  commit, machine-drafted and marked for native review where you are unsure — that is the repo's
  standing policy.
- If you run the same test more than about five times without progress, STOP and report.

## Gate commands

| ID | Gate | Command | Baseline on `main` |
|---|---|---|---|
| G1 | build | `npm run build` in `web/` | clean |
| G2 | tests + coverage | `npm run test:coverage` in `web/` | `Test Files 110 passed (110)` / `Tests 2411 passed (2411)`; statements 90.77 **branches 86.04 functions 85.84 (0.84pt headroom)** lines 93.67 |

**G2's functions floor has under one point of headroom.** You are adding a component and new branches.
**On a coverage trip: STOP and report the four numbers. Do not edit `vite.config.ts`. Do not add a test
whose only purpose is to raise a number** — cover the code you added, properly, or report that you
cannot.

`tools/simulation/ui/` has no `node_modules` in this worktree, and the sim stack is not yours to rebuild.
You do not run Playwright; you **read** those specs to check callers, per #394.

---

# INCREMENT 1 — a farm-clock relative time helper (#653)

## The decision you are implementing, and why it is not the obvious one

`ProvenanceCell` renders UTC **deliberately**. Its own comment says: *"Timestamp rendering follows
AuditPage's existing convention (UTC, trimmed to seconds) rather than the farm clock: these are audit
instants, and the trail is displayed in UTC everywhere else it appears."* That came out of #494's review.

Issue #653 asks for relative time on the farm clock. Both are right, about different things, so:

- **The relative phrase uses the farm clock.** "2 days ago" is a statement about *when, relative to the
  reader's day*, and a reader in the hen house means their farm's day. `useFarmToday()`
  (`web/src/farm/useFarm.ts`) is the existing source.
- **The exact instant stays UTC**, in the `title` attribute, unchanged in format. #494's decision
  survives untouched for the precise stamp; only the human-scale summary is farm-relative.

Put that reasoning in a comment on the helper. A future reader will otherwise "fix" one of the two.

## 1a. RED — the helper's tests first

Create `web/src/lib/relativeTime.test.ts` before the helper. Test, at minimum: same day, yesterday, a few
days, about a week, over a month, and a future instant (clock skew is real — decide and pin what it
renders rather than leaving it to chance). Drive it with an injected "now" so it cannot be flaky.

Run it and record the failure — it must fail because the module does not exist yet, not for any other
reason. **If it fails differently, STOP and report.**

## 1b. GREEN — the helper

Create `web/src/lib/relativeTime.ts`. Requirements:

- Takes an ISO instant and a reference point; returns a translated relative phrase.
- **Uses i18next plurals in the repo's established form** — `key_one` with the base key as the other
  case; `web/src/i18n/en.ts` around line 368 has the convention and a comment explaining it. Do not
  invent a different pluralisation scheme.
- Day boundaries are computed on the **farm** timezone, not the device's.
- No new dependency. `Intl.RelativeTimeFormat` is available and is the framework facility to prefer over
  a hand-rolled table — the repo's standing rule is to reach for the built-in first and justify anything
  hand-rolled. If you conclude it does not fit (plural rules, locale coverage for `tl`), **say why in a
  comment and in your report** rather than silently hand-rolling.

Strings in `en`, `es`, `tl`.

## 1c. Commit

```bash
git add web/src/lib/relativeTime.ts web/src/lib/relativeTime.test.ts web/src/i18n/
git commit -m "feat(web): a farm-clock relative time helper (#653)"
```

---

# INCREMENT 2 — ProvenanceCell to one line (#653)

## 2a.

`web/src/components/ProvenanceCell.tsx` currently renders up to three `<div>`s of full sentences. Make it
**one muted line**: relative time + actor, e.g. `2 days ago · sim-sales-1`. Requirements:

- The **full stamp stays reachable** — `title` on the cell, carrying what the three lines say today, in
  UTC. Nothing that was visible becomes unavailable.
- The three-line structure's *information* is preserved in the title: created, last changed, and the
  official step where there is one.
- The actor is the local part of the email, not the whole address — `sim-sales-1`, not
  `sim-sales-1@sim.local`. Column width is the point of the issue.
- The placeholder branch (`—` when there is nothing to say) is unchanged.
- `white-space: nowrap` and a max width on the column, so it can never again be the widest thing in the
  table.

**Read the existing tests for this component before you change it** and keep every guarantee they pin.
`#494`'s rule still binds: whether a change happened is the **server's** call via `lastChanged*`; do not
re-derive it by comparing timestamps.

## 2b. Callers

Five screens render it: `GradesPage`, `FlocksPage`, `ExpensesPage`, `HistoryPage`, `SalesPage`. Check each
still reads correctly with a one-line cell.

**Driver already swept the Playwright surface: no spec in `tools/simulation/ui/specs/` reads provenance
text.** Confirm that yourself with a grep before relying on it, and say in your report whether you agree.

## 2c. Tests, then commit

Vitest for the new rendering, including the title's contents and the nowrap. Then G1 and G2.

```bash
git commit -m "feat(web): provenance in one line, full stamp in the title (#653)"
```

---

# INCREMENT 3 — date-range filters into a bounded toolbar (#653)

`.toolbar` already exists in `web/src/styles.css`, fully styled, with **zero consumers** — it was styled
ahead of this slice by #651 and carries a comment saying so. **You are adding the consumers. Do not
restyle it**; if it looks wrong when used, that is a finding to report, not to fix here.

Screens with date-range filters: **Reports, History, Audit, Feed, Water, Expenses** (#653 names these).
Wrap each screen's date-range controls in a `.toolbar` row, `max-width: 12rem` per date input, side by
side. Also give the reference/order-number cell `white-space: nowrap` so `SO-66C100AD` stops splitting.

Update the comment on `.toolbar` in `styles.css`: it currently says the rule has no consumers and points
at this issue. That is now false — rewrite it to describe what it is for, and delete the "check
`grep -rn toolbar` before assuming it is unused" line, which has served its purpose.

Tests for the layout facts jsdom cannot see go in a `styles.*.test.ts` using `postcss`, exactly as
`web/src/styles.num.test.ts` and `web/src/styles.elevation.test.ts` do — read one of them first.

G1, G2, then commit:

```bash
git commit -m "feat(web): date-range filters in a bounded toolbar (#653)"
```

**STOP HERE if this is the first dispatch.** Report and wait; the driver dispatches #655 separately.

---

# INCREMENT 4 — the EmptyState component (#655)

## 4a. Which sites are empty states

There are **43** `<p className="muted">{t(...)}</p>` sites under `web/src/routes/`. **They are not all
empty states.** An empty state is one rendered *in place of a list or table when it has no rows*. An
inline hint, a helper sentence under a field, or a note beside a control is not, and converting one would
be wrong.

**Enumerate them first and classify each**, then put the classification in your report before you change
anything: path, line, and empty-state or not, with a word of reasoning. That list is the deliverable of
this step; the edits follow from it.

## 4b. The component

`web/src/components/EmptyState.tsx`: a lucide icon (already a dependency), one sentence saying what will
appear here and why it is empty, and the screen's **primary action** as a button — the same handler the
page-head button uses, not a duplicate implementation.

Two variants, distinguished on screens that have filters: **filtered to nothing** (offer "Clear filters")
versus **nothing exists yet** (offer the create action). **Role-aware:** a user who may not perform the
action gets the sentence with no button, matching the page-head's existing gate — read how that gate is
written on one screen and reuse it rather than re-deriving the role check.

Copy in the interface's voice: says what to do, no apology, no mood. `en`, `es`, `tl` inline.

## 4c. Tests

Component tests plus one integration test per variant. This is where the coverage headroom gets spent —
cover the branches you add.

---

# INCREMENT 5 — roll it out (#655)

Replace every site your 4a list classified as an empty state. `#655` asks for a grep-pinned guarantee that
no bare `<p className="muted">` empty state remains — write that guard against **your classified list**,
and be honest in its comment that it pins the empty-state sites specifically, not every muted paragraph,
because the latter would be wrong.

Check `tools/simulation/ui/specs/` for specs asserting the old empty-state text and update them (#394).

# INCREMENT 6 — gates, PR

G1 and G2 clean against the baseline. Push, and open the PR titled exactly:

```text
feat(web): one-line provenance, bounded date filters, and empty states that invite action (#653, #655)
```

Body: what changed per issue; your 4a classification list; the Playwright caller findings; G1/G2 output
with the four coverage numbers; and any acceptance criterion you did **not** meet, named.

Do NOT merge. Do NOT mark ready or draft.

## Report back

Branch, commit SHAs, `git status --short`, G1/G2 output, the classification list, caller findings, PR URL,
and anything here that turned out to be wrong.
