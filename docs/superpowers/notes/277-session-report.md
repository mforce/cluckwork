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

## Exact next step

Run review round 2 — the full four-way, on the fix diff
(`git diff 500e37b..HEAD`), telling reviewers these commits are fixes for named
findings so they check the fix rather than re-litigating the original. Then work
the seven open codex findings, starting with the two `session-races` ones.
