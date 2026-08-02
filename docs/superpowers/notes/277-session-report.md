# #277 — session report

Unattended run, 2026-08-02. Decisions and assumptions: `277-decisions.md`.

## What landed

Three stacked draft PRs, all pushed. **The owner merges; nothing here was merged.**

| PR | Branch | Issue | State |
| --- | --- | --- | --- |
| **#390** | `feat/277-e2e-smoke` | #385 | draft — Playwright harness + smoke suite |
| **#391** | `feat/386-canary` | #386 | draft — canary + Core Web Vitals (stacked on #390) |
| **#392** | `chore/387-e2e-ci` | #387 | draft — `workflow_dispatch` CI (stacked on #391) |

Slice issues **#385 / #386 / #387** filed and added to epic **#15**'s checklist under the existing #277 bullet.

## What is green — measured, not asserted

```
smoke suite   : 28 passed, 1 skipped (opt-in 15-min spec), ~32s
canary        : 4 passed
typecheck     : clean
mutation      : baseline GREEN -> 10 killed / 0 survived -> restore GREEN
```

Re-run three consecutive times after the last fix to confirm the audit spec's
intermittency was gone, not merely unobserved.

## The blocker I caused, and its remediation

**I committed four Playwright trace archives containing live credentials.** Three
of the four reviewers caught it independently.

- Confirmed by extraction: **36 plaintext occurrences of the Owner password, 60
  JWTs, 24 refresh-cookie entries.**
- Cause: `.gitignore` listed `playwright-report/` — a trailing-slash pattern is a
  literal directory name and never matched `playwright-report-canary/` — combined
  with `git add -A`.
- **GitGuardian passed the PR.** It does not scan inside a zip. Worth knowing.

Remediation, all verified rather than assumed:

1. Files removed; ignore patterns widened to globs.
2. Credentials rotated. The first attempt **silently did nothing** —
   `bootstrap.sh --force` exits with `requires openssl on PATH` on this NixOS box,
   and I only noticed because the cast file's `generatedAt` had not moved. Re-run
   under `nix-shell -p openssl`.
3. Rotation proven: I recovered the leaked password *from the committed trace
   itself* via git history and re-tested it — **401**. The new one — **200**.
4. Two more instances of the same class, found by codex and fixed: `reset.sh`
   echoed `bootstrap-admin`'s one-time password (unmasked into every CI log of the
   new workflow), and the workflow uploaded trace archives as a retained artifact.

**Open, and the owner's call:** the blobs remain in these branches' history.
Purging needs a force-push, which is on my never-do list. The credentials are
throwaway, local-only, and now inert, so the exposure is neutralised — but the
history is not clean.

## Findings filed rather than absorbed

- **#388** — worker flock assignment is enforced on **writes**
  (`422 FlockScope.NotAssigned`) and on **no read path**: a restricted worker sees
  both flocks, either flock's detail, and another flock's 90 days of history, all
  `200`. #277's Worker premise is therefore not what the app does. Established by
  probing the live fixture, and confirmed by grep: `IUserRoleAssignmentRepository`
  has four consumers and none is a query-side feature. The spec asserts the
  guarantee that exists and links here.
- **#389** — `UsersPage` renders its load failure without `role="alert"`, unlike
  every sibling screen, so a screen-reader user is refused in silence.
- **Not filed, needs a decision:** a daily entry dated **before its flock's
  placement date** is accepted (`201`). Found incidentally while probing; I did
  not chase it because it is outside #277's scope and I could not tell whether it
  is intended.

## The review round

Four reviewers: codex, `feature-dev:code-reviewer`, `caveman:cavecrew-reviewer`,
and pi. Consolidated response posted to all three PRs, tagging `@codex`.

**pi needed two retries** — `deepseek-v4-flash` returns a `RegionError` (China-only
opt-in) and `-p @file` with a separate positional prompt produced empty output.
What worked: `glm-5.2`, with the prompt and diff bundled into a single `@file`.

**Six vacuous assertions were found and fixed** — each would have stayed green with
the guarantee removed. The two worth remembering:

- The **PWA denylist** spec probed with `fetch()`. `navigateFallback` only governs
  requests whose mode is `navigate`, so it was testing nothing; deleting the
  denylist entirely left it green.
- **Row counts used `getByRole("row")`**, which counts the `<thead>` row — so
  `not.toHaveCount(0)` was satisfied by a table with no data in it.

**Two defects in my own mutation harness:**

- `KILLED` was awarded to *any* failure. An uncaught `TypeError` reports as
  `1 failed` exactly like a failed expectation, so the check that claimed to
  exclude crashes did not. Now requires an assertion-shaped failure; anything else
  is `INCONCLUSIVE` and counts as **survived**.
- `mutants.ts` claimed nav-gate coverage via "spec-level vacuity mutants" that did
  not exist. The comment was actively hiding the gap it described.

**A mutant survived on its first run** (`nav-role-gate-bypassed`) and the harness
reported it rather than hiding it — forging the login token is not enough, because
the bootstrap refresh replaces it before the nav renders.

**One fix was itself wrong.** My first audit-filter fix asserted a strict row-count
drop. It passed alone and failed **intermittently** in a full run: the audit log
paginates *and* the other specs write to it mid-run. Replaced with a count-free
assertion. This is the round-2 lesson arriving early — findings against your own
fix are the most likely to be real.

## Deferred, with reasons

- **The PWA update prompt has no end-to-end coverage.** Playwright sees the initial
  `sw.js` registration fetch but **no request at all** after
  `registration.update()` — measured in both directions, and not a caching
  artefact. The logic is unit-covered; the browser genuinely parking a second
  worker is not. Reversal path in `277-decisions.md`.
- **`run-canary.sh --with-load` has not been exercised against a full k6 baseline.**
  The quiet path is green and the findings-doc rendering is verified end to end via
  `--render-only`; the k6 orchestration is not.
- **The CI workflow has never been dispatched.** It cannot be until it exists on a
  branch GitHub will offer. The first dispatch is the first proof.
- **Seven codex findings remain open**, listed in full in the PR comment. Four are
  further vacuous-assertion candidates (`session-races` ×2, `reports-range`,
  `i18n` durable preference) and are where I would go next.

## Honest notes

- I asked one mid-run question (the #388 premise) rather than assuming, because it
  changed what shipped.
- Write-flow specs mutate the fixture, so `SimulationDataSeeder`'s exact-count
  check reports `Failed` rather than `AlreadySeeded` after a suite run. Expected;
  `reset.sh` clears it.
- The fixes were distributed across the stack by **merge, not rebase** — the
  branches were already pushed and rebasing them would need a force-push.

## Round 2 — ran, and it mattered

Full four-way again on the fix diff. **Every finding that mattered was against
round 1's own fixes.**

- **A new high the first round could not have seen:** the Playwright HTML report
  is credential-bearing *by construction* — Playwright embeds a base64 copy of the
  run in `index.html` and renders step titles containing literal fill values, so a
  sign-in step reads `Fill "<the password>"`. Round 1's "strip the trace zips" was
  a fix that looked complete and was not. CI no longer uploads the report.
- **The strip step failed open** (`|| true`, upload runs `if: always()`) — a
  mechanism with no enforcement. Now a real gate.
- **The mutation harness was wrong twice, in opposite directions.** The round-1
  grep matched `expect(` anywhere in the log, including the code frame Playwright
  prints around *every* failure — a reviewer reproduced a `TypeError` reporting as
  `KILLED`. Anchoring it then demoted three genuine kills to `INCONCLUSIVE`,
  because Playwright prints the matcher inline after `Error: ` when no custom
  message is given. **The harness caught the second one on itself.**
- **`nav-role-gate-bypassed` is a false kill** — the forged token is rejected by
  the server, so sign-in fails before the nav assertion runs. Nav gates are now
  recorded as unmutated. Second withdrawal of that claim; the generalisable reason
  is in the file.
- The audit-filter assertion is on its **third** revision (vacuous `<=` →
  intermittent count → ordering-coupled option pick + mid-refetch read → derived
  target + polled settle).

Final state, verified: smoke 28 passed / 1 skipped, canary 4 passed, typecheck
clean, mutation baseline GREEN → **10 killed / 0 survived** → restore GREEN.

## Round 3 — and a result of mine that had to be withdrawn

Three legs: codex, `feature-dev:code-reviewer`, `caveman:cavecrew-reviewer`.
**pi did not run** (local vllm down) and the PR comment says so rather than
implying a full round.

Codex answered round 2 by **writing code instead of reviewing**. Nobody asked it
to. The cause was mine: every comment ended with a "Still open" checklist, which
the connector reads as a work queue. Comments are now explicitly review-only and
open items live here instead. Its output was kept, at the owner's direction, and
reviewed like any other contribution — which is how the next item surfaced.

**The retraction.** Mid-round I reported that the corrected `session-races` spec
killed the `client.ts` mutant and that the fix was proven load-bearing. It was
not, in two successive ways:

1. `releaseHeldRefresh()` sat AFTER the nav assertion. On a build without the
   abort, nothing settles the held refresh, `isLoading` never clears, and the
   spec died on the nav timeout with the release unreached. **A red for the wrong
   reason, read as coverage.**
2. Moving the release earlier made the mutant SURVIVE — and the reason is the
   instrument. `route.continue()` re-issues the request and the browser
   re-attaches the cookie jar **as at send time**; `allHeaders()` keeps reporting
   the creation-time snapshot, which is exactly why this was convincing for three
   rounds. Measured on the bug-present build: the header read as the Owner cookie
   at both intercept and continue, while the response rotated the **Sales** chain
   and the reload restored Sales.

So the late-`Set-Cookie` race **cannot be expressed with route interception at
all**. Three placements were tried; none can work. Recorded in the spec header
with the evidence, plus an instruction not to move it again.

**Consequence:** `login()`'s `abortInFlightRefresh()` has **no end-to-end
coverage**. Its only coverage is `client.test.ts`, which passes partly because
its fetch mock ignores `AbortSignal`.

Also fixed this round:

- **PR #390 still shipped the `.gitignore` that caused the credential leak.**
  Round 1's globbed patterns reached #391 and #392 only. Fixed at the root of the
  stack so it propagates by merge.
- Both races release in a `finally` — a throwing assertion stranded the
  interception and buried the real error under a timeout.
- The specs no longer exercise `sessionGeneration` at all; with the abort in both
  `login()` and `logout()`, deleting the counter leaves them green. The file
  called itself that counter's regression layer.
- **i18n's reload assertion was vacuous** — `i18n/index.ts` seeds from a
  localStorage hint synchronously, so `<html lang>` was right before any network
  call. The hint is now cleared first.
- `client.ts`'s comment claimed a guarantee it cannot deliver (the abort is a
  no-op for a refresh queued behind another tab's Web Lock). Qualified.
- The mutation harness counted a known false kill in its headline: runs reported
  `10 killed` when **9** proved anything.
- The canary completeness check was a second copy of the screen list; the
  manifest now travels in the sample data.

Verified: typecheck clean, two consecutive full runs 28 passed / 1 skipped,
mutation baseline GREEN → 9 real + 1 false kill, 0 survived → restore GREEN.

## Exact next step

**Round 4.** Round 3 found real defects in round 3's own fixes, so the loop has
not converged.

Open, and carried rather than dropped:

1. **`client.ts` is now weaker than when it was kept.** It has no E2E coverage,
   its unit test does not exercise the abort's rejection path, and an
   aborted-but-server-committed rotation can revoke every session for that user
   if the login then fails. Reverting it to its own #310 issue is defensible.
2. **The audit spec fails on the first run after a fresh reseed** and passes
   after. Fixture-order dependence, not flakiness — it needs rows earlier specs
   create. Fourth revision of that one assertion; stopped rather than guess at a
   fifth.
3. The four trace blobs remain reachable via ancestor `076bf3c`. Purging needs a
   force-push — owner's call. Credentials rotated and verified dead.
4. Never exercised: `run-canary.sh --with-load` against a real k6 baseline, and
   the CI workflow's first dispatch.
5. A daily entry dated before its flock's placement date is accepted (`201`).
   Owner ruled this **intended**; not filed.
