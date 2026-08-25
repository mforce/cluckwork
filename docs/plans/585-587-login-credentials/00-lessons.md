# Lessons — #585 + #587: farm-qualified credentials and revocable remembered farms

Opened at Phase 0 by the driver.

Mode: feature. Dispatch contract from Phase 0: implementer Pi / `llamacpp:qwen3.8-27b-q5-xl-220k-q8kv`; reviewers login/privacy invariants and false-green browser/test evidence; fix budget 0 including transient verification edits; background visibility Herdr tabs in the current workspace, closed when no longer needed; external review trigger none specified.

---

# PART 1 — CAPTURE

## Events

- 2026-08-24 — Initial sandboxed Pi inventory probe could not acquire Pi's configuration lock; the owner confirmed the selected Qwen instance interactively and supplied its exact model id.
- 2026-08-24 — Owner required zero driver edits, including transient verification edits; any mutation must therefore be performed by a non-implementer in an isolated throwaway worktree.
- 2026-08-24 — Driver initially described a hidden composite username as following browser guidance too broadly. The guidance supports hidden *actual* usernames; a generated tenant-plus-email credential key is not standardized and must be framed as a Chrome-version-specific manual verification, not cross-manager compatibility.
- 2026-08-24 — Owner directed autonomous, in-scope execution and asked not to receive routine permission questions; retain only required design/merge gates and genuine conflict questions.
- 2026-08-24 — Independent code review of the draft PR (#598) produced REQUIRED findings: a raw-string field-clear comparison that missed canonical (case/padded) forms; a `removeFarmCode` path that wrote `[]` over an unreadable roster when `getItem` threw but `setItem` would succeed; a Forget glyph on `--danger` over `--surface-2` at 2.76:1 (dark theme); an inline-axis floor of only 36px; and an un-amended epic T7a row. All five were fixed in a follow-up fix commit on the same branch (2026-08-25).
- 2026-08-25 — During the fix round, the review correction itself was caught mid-flight: the first malformed/non-array removal tests proved their point through a follow-up `rememberFarmCode` call, which would normalise the same storage and false-green a no-op removal. Corrected to assert the raw stored text directly after `removeFarmCode`.

## Facts that decay

| Thing | Value |
|---|---|
| Branch / PR | `feat/remembered-farm-forget` / draft PR #598 (opens #587; #585 closed as deliberate won't-fix) |
| Head SHA at each verification | `06653c6a` (initial commit, all verification green); fix commit on the same branch (2026-08-25) — re-verified: typecheck, 1897 unit tests, coverage (auth 98.93/95.31/100/100 vs floor 98/90/100/100), build, `verify:sw`, `i18n:scan`, all green |
| Suite counts: before / after / tests added | 1881 → 1897 (+16) between the two verification runs. Net only — the earlier per-commit attribution in this cell miscounted `it.each` expansions (the fix commit's `it.each` guards add 2 tests, not 1, per axis) and the suite grew on `main` in between, so no per-commit breakdown is recorded |
| Rounds: review rounds run, fix rounds, who found what | 2 independent review passes / 1 fix round. Pass 1 (PR authoring): found the 44px single-axis floor, the destructive chip risk, and the identifier gaps. Pass 2 (independent, 2026-08-25): found the raw-string clear (canonical `Farm-A` vs `farm-a`), the `[]`-over-unreadable-roster write, the `--danger`-over-`--surface-2` contrast failure in dark, the missing inline-axis floor, and the un-amended epic row. All five fixed in one commit; the contrast fix chose `--error` (4.99 light aubergine / 6.44 dark) over `--danger` (6.03 / 2.76) and pinned both the at-rest and hover pairs in `styles.test.ts`. |
| Mutations: run, red as named, surviving, not run | 8 run, 8 red as named, 0 surviving, 0 not run. Initial commit (5): M1 broken filter predicate → raw-storage removal test; M2 bypassed `if (!accepted)` → cancellation test; M3 removed `current === code` clear branch → prefilled-clearing test; M4 removed `min-block-size: 44px` → styles guard; M5 each of the six `id`/`name` attributes removed one at a time → identifier test. Fix commit (3): raw `current === code` restored → canonical field-clear test; `readRawRoster() === null` no-op removed → the throwing-`getItem` no-op test plus the malformed/non-array raw-text tests (all four redden together, which is correct: they assert the same read-failure distinction); `min-block-size: 30px` and `min-inline-size` removed/shrunk → the two-axis `it.each` guard plus the every-rule guard. No markers left (`rg -n "MUTANT" web/src` empty). |
| Direct driver applications, with budget count | 5 of 0 — the owner's fix-budget-0 contract was overridden by the owner's own explicit instruction to act as sole implementer for the review fixes (2026-08-25). Recorded so the budget figure is not silently rewritten as if it had held. |
| Reviewers that never answered, and why | The dispatch-named login/privacy and false-green browser/test reviewers never answered — the independent code review that produced the REQUIRED findings arrived as a direct owner instruction instead, with no separate review ledger. |
| Deferred findings and unmet criteria, with issue numbers | #585's farm-qualified password-manager behaviour is the unmet criterion, deliberately declined (no standards-backed manager storage-key contract for a tenant identifier; decision recorded in the #530 amendment comment `#issuecomment-5400679046`). The ADR accepted-disclosure revision is deferred to #537 per the #587 amendment. The native-speaker es/tl review of the machine-drafted copy remains pending (the tl "Forget control" → "kontrol na Kalimutan" rephrasing is part of that same review queue). |

---

# PART 2 — RETRO

## What worked

- **The design's "never rejects" contract held under mutation.** Every degradation path (unavailable storage, rejected lock, malformed JSON) has a named test, and the two paths the review found — the read-failure wipe and the raw-string clear — were both *adjacent* to declared contracts, not inside them. The contract text in `farmCodeCache.ts` ("a failed READ is a no-op, not an empty-array write") now states the distinction the tests prove.
- **The selector-aware PostCSS guard earned its weight.** The original single-axis guard was written to the "walk everything, exclude deliberately" shape from the start (real DOM topology, comment-stripped selectors, at-rule refusal), which is what let the fix round extend it to two axes in one `it.each` rather than rewriting it. The mutation proof (shrink block, shrink inline, drop inline) all reddened the named tests.
- **Asking the DOM to match selectors, not parsing them, caught what a text search would not.** The guard's `el.matches(cleanSelector(...))` is why an equivalent-but-differently-spelled selector cannot hide a sub-44px declaration.

## What cost

- **A test that proves through its own fix is a false green.** The malformed/non-array removal tests' first draft asserted the storage state *after* a follow-up `rememberFarmCode` call — which normalises the same key. A no-op removal would have passed. The fix (assert the raw stored text immediately after `removeFarmCode`) was caught by the owner's correction, not by a mutation: the mutation "no-op removal" does not red the follow-up-normalised assertion, and no mutation in the runbook covered that shape. **Lesson: when a test's fixture and its assertion share a normalising writer, the assertion must read the store before any other writer touches it.**
- **The 44px target was declared in one axis and guarded in one axis.** `min-block-size: 44px` plus `min-inline-size: 2.25rem` (36px) reads as "44px minimum" in the CSS comment and is 36px wide in the browser. The guard mirrored the code because the guard and the code were written from the same assumption. **Lesson: a size guarantee stated as one number is two axes; name the axes in the CSS comment, the test, and the PR body.**
- **The contrast failure was invisible to every test in the suite.** `--danger` over `--surface-2` at 2.76:1 in dark theme is a token-pair property, not a selector property — the style guard cannot see it, and no test computed the pair until the review named it. The fix adds the pair to `styles.test.ts`'s existing per-brand, per-mode contrast loop (at-rest `--error`/`--surface-2` and hover `--on-danger`/`--danger`), which is the repo's established shape for exactly this class of guarantee.
- **The budget contract and the owner's correction collided.** Fix budget 0 meant "the implementer does not edit source for verification"; the owner then instructed the same session to implement the review fixes. The lesson file had to be updated to record the override rather than silently report 0.

## What to carry

- For a storage read-modify-write, the **read-failure and read-empty distinction is load-bearing**: `null` (unreadable) means no-op, `""`-parses-to-empty means write `[]`. A single `readFarmCodes()` call cannot express it; the private `readRawRoster()` can. Any new roster writer must inherit the same two-outcome read or re-derive the distinction.
- **Compare canonical, never raw, when the state being cleared came from a canonical store and the value being compared came from a human-typed field.** The roster holds canonical codes; the field holds raw typing. `canonicalFarmCode(current) === code` is the only comparison that survives both.
- A **fix round's mutation run must include the mutations the review found**, not just the runbook's original table: the raw-string clear and the read-failure no-op were not in the original runbook's five, and both had a red test only after the fix added them.
- The **epic-amendment rule (AGENTS "keep phase epics in sync") applies to the epic body's checklist rows, not just the issue it ships**: T7a's checkbox and state text had to be amended in the same PR that closed #585, or the epic would keep claiming a requirement the decision record had declined.
