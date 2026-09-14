# #822 design doc: arena synthesis record

**Artifact:** `docs/designs/822-mui-revamp.md`. **Base commit:** `b0638e1` (`feat/674-mui`, PR #860).
**Method:** three independent drafts (Claude Opus 5, Claude Fable 5.1, Claude Sonnet 5) against one brief
with the verbatim text of #822 to #830 and #740, one read-only cross-judge on Opus 5 scoring a
seven-row rubric and spot-checking every inter-candidate disagreement against the code, then a
hand merge by the driving session. Candidate texts, brief, rubric and judge file were kept under
`/tmp/arena-822/` and are not committed.

## Base

Candidate 2 (Fable). It scored 3 of 3 on five of seven rubric rows, decided everything #823 and #656
ask, and was the only draft whose #824 answer shipped a new guard with mutation rows rather than a
table of intentions. Candidate 1 (Opus) scored 3 on four rows; candidate 3 (Sonnet) on none.

## Disagreements, and which the code supported

| Point | Candidates | Verified |
|---|---|---|
| `.panel` call sites | C2: 4, all Dashboard. C3: 4 screens. C1: 19 across three classes | C2. C3's grep matched `.panel-actions`; C1 conflated `.card`/`.order-panel` (actual 10). |
| Style guards | C1: 10 postcss of 12+ readers. C2: 11 postcss. C3: 10 | C1. `styles.test.ts` reads via `cssTokens`, not postcss. |
| `Dialog` call sites | C2: 13 files / 58. C1: 30 | C2. |
| `title=` attributes | C1: 41 native tooltips. C2: 2 native, rest Dialog prop | Neither exactly: 42 in 18 files, roughly a dozen on HTML elements. Doc says "classify per site". |
| Floating UI under `Tooltip` | C1: Popper. C3: Floating UI `shift()` | C1. Lock resolves `@popperjs/core`, no `@floating-ui`. |
| Inter `opsz` | C2: not loaded, +118.9 KiB. C3: `font-optical-sizing: auto` on today's face | C2. `index.css` loads zero `opsz` faces. C3's setting grafted as the preferred value once the face is loaded. |
| `CssBaseline` | C1: decline. C2: adopt in #823. C3: reaffirm the comment | Fact agreed by C1 and C2 (only real delta is `body1` line-height). Doc keeps C2's decision, carries C1's comparison table, and marks it owner review. |
| #740 fix location | C1: #830. C2: #823. C3: partial via #830 | C2. The cause is one app-wide rule pair; `phone.spec.ts:243-247` assigns the remedy to #674. |
| Bundle ceiling | C1: 1,600. C2: 1,800. C3: none | Not checkable without a build. Doc keeps 1,800 (evidence-derived) and names 1,600 as the alternative, owner review. |
| More sheet | C1, C2: `SwipeableDrawer`. C3: keep `Dialog` | Code-neutral. Doc keeps `SwipeableDrawer`, carries C1's `anyDialogOpen()` cost, names C3's alternative, owner review. |
| `NumberField` | all: none in `@mui/material`. Owner note allows alternatives | Doc names both `TextField`+`IconButton` and Base UI `NumberField`, owner review, measured cost decides. |

## Grafts

From candidate 1: the ten bare element selectors (§2.3) and their consequence for #823; the
Popper correction; the #740 evidence block (English reproduction, `phone.spec.ts` instruction,
`phone-action-label-wrapped` mutant); the jsdom fallback-palette trap (6 of 17 values differ);
the coverage-ratchet trap (D10); the `CssBaseline` comparison table; the four-radius defect in the
bridge; the `BusyButton` sibling live-region reason; the `Autocomplete` English default strings.

From candidate 3: the elevation guard is self-detecting (equality assertion); `emptyStates.guard`
as the existence proof for source-shape guards; `GradingChip` is a drag source; `font-optical-sizing:
auto` over a fixed `"opsz" 32`.

Owner direction received mid-arena (alternatives acceptable where MUI has no one-to-one control,
each reviewed): applied as the "owner review" marker on D2 rows 3 and 13 and collected in §7.

## Rejected

Candidate 3's "shared rule dies with its last consumer" section, because its premise (four `.panel`
consumers) was a grep artefact. Candidate 1's 1,600 KiB ceiling as the decision, kept as the
alternative. Candidate 1's `title=` count. Candidate 2's "eleven postcss guards".

## Dropouts

None. All three candidates produced both files.

## Verification

Every count the doc states was either produced by a quoted command at `b0638e1` or spot-checked
by the judge or the driving session. The interrogate pass and its outcome are recorded in the PR.
