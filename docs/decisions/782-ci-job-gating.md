# Skip the web and image jobs on documentation-only pull requests (#782)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-12
**No incident** — accepted-risk record (no defect shipped; deliberate declination).

## What happened

Nothing broke. This is a forward-looking choice, recorded because the obvious
version of it would break the release pipeline and a later reader is entitled to
know which parts are load-bearing before simplifying any of them.

The measurement behind it, from #782: 7 of the last 40 merged pull requests
(#701, #718, #731, #744, #754, #757, #768 — about 17%) changed nothing under
`src/`, `web/`, `tests/`, `tools/`, `deploy/`, `.github/`, `Directory.*` or the
solution file. Per-job wall clock on a green `main` run was `Build and test`
568s, `Web typecheck, test, and build` 213s, `Image build + Trivy scan` 129s. So
the two jobs gated here are roughly 340 of the ~900 seconds a documentation-only
pull request spends today, and they are the part that no amount of prose can
affect.

`Build and test` is deliberately NOT gated, and this is the part that makes the
naive version of the idea wrong. In this repository documentation is not outside
the test suite's scope, it is something the suite polices. Four gates are
load-bearing on a markdown-only change: `SchemaDocsTests`'s image-pin sweep and
its Redis twin enumerate `git ls-files -z` with no extension filter, so every
tracked `.md` is in scope — `AGENTS.md` records that guard going red on a plan
document and costing a full implementer stop one increment from the finish line
(#508); `TenancyDocsFreshnessTests.NoTrackedFileDescribesTenancyAsDormant` runs
the same tracked-file sweep; GitGuardian scans the diff, and a runbook is an
ordinary place to leak a credential; and `tools/schema-docs/generate.sh --check`
exists precisely because `docs/schema/` is generated and must never be
hand-edited (#417). Skipping `Build and test` on a documentation pull request
would stop all four running on exactly the changes they were written to catch.

## The rule

`ci.yml`'s `changes` job computes one predicate, `docs_only`, and `web` and
`image` carry `needs: [changes]` plus
`if: ${{ !cancelled() && needs.changes.outputs.docs_only != 'true' }}`. **The
gate is `pull_request`-only**: the classify step short-circuits to
`docs_only=false` on every other event without consulting git, because `publish`
declares `needs: [tests, build-and-test, web, image]` and a job whose `needs` dependency
was skipped is skipped too. A push to `main` that skipped `image` would publish
no `ghcr.io/<owner>/<repo>:sha-<commit>` for that commit, and per #351 a release
drafted at a commit with no image can never be promoted — it stays a draft with
no git tag, recoverable only through the repair dispatch at the top of `ci.yml`.
**Every failure direction resolves to "run both jobs."** An unclassified path is
code, an empty diff is code, an unreadable stdin is code, a git or node failure
inside the step still writes `docs_only=false`, and a `changes` job that somehow
failed anyway leaves its outputs as empty strings, which `!= 'true'` runs on.
Break either half of that `if:` and the gate becomes fail-open.

**`changes` carries `continue-on-error: true`, and that is a correctness
requirement rather than tidiness.** GitHub's `needs` documentation says "a
failure applies to all jobs in the dependency chain from the point of failure
onwards", and `publish`'s `if:` uses no status-check function, so it carries an
implicit `success()` over its ancestors. Making `web` and `image` depend on
`changes` therefore put a NEW job into `publish`'s ancestry, and a `changes` that
failed for any reason — a flaky checkout, a lost runner, its own five-minute
timeout — would skip `publish` on a push to `main` even though `web` and `image`
ran and passed under `!cancelled()`. That is the no-image-on-`main` hazard
arriving by a second route, opened by the fix for the first. `continue-on-error`
closes it: the job's conclusion is success whatever happens inside it, so there
is no point of failure for the chain rule to propagate from.

**The classifier's self-test is a separate job with no dependents**
(`classifier-self-test`). It cannot live inside `changes`, because a
`continue-on-error` job cannot fail a run and a guard that cannot fail a run is
not a guard. It must also not become a `needs` of anything `publish` depends on,
or a broken test file would reintroduce the same skip chain. In its own job a red
self-test paints the pull request red, which is all it needs to do.

**`git diff` runs with `--no-renames`.** `diff.renames` defaults to true, and
with detection on `--name-only` prints only a rename's DESTINATION. Measured on
this repository: `git mv src/Cluckwork.Domain/Common/Result.cs docs/Result.cs`
produces the single line `docs/Result.cs`, which classified as
documentation-only while a source file had in fact been deleted. Two guards hold
the flag in place, because the module and the workflow each had their own copy of
it: an end-to-end test that performs that exact `git mv`, and a test that reads
`ci.yml` and asserts the one classifying `git diff` line carries the flag.

## Why not the obvious alternative

**Enumerate what each job needs.** The first reach is a positive trigger list per
job: `image` runs on `src/**`, a `.csproj`, a `packages.lock.json`, the
Dockerfile; `web` runs on `web/**`. #782's own decision 3 warns that those two
lists are not the same and that deriving one shared condition for both would be
the easy mistake. Both problems come from the same root: a positive list is a
hand-maintained record of what someone thought of, which is the shape
`AGENTS.md`'s "Writing a guard" section tells you to replace with "walk
everything, exclude deliberately." Inverting the question removes the failure
mode rather than managing it. `isDocsOnly` asks whether EVERY changed path is
documentation, so a path nobody has classified is code, a new top-level
directory runs the full suite on the day it appears, and one condition is
correct for both jobs by construction rather than by a coincidence that could
drift. #782's decision 2 — that the gate's path set would be a second copy of
`e2e-smoke.yml`'s `paths:` filter and the two would drift — dissolves the same
way: this set is not that set and is not trying to be, because it enumerates
documentation rather than code.

**Use `on.pull_request.paths`.** A path filter gates the whole workflow, so it
cannot skip two jobs and keep `build-and-test`, which the section above shows is
mandatory. It also leaves a REQUIRED status check that lives in the filtered-out
workflow stuck in `Expected` forever, so the pull request can never merge; the
usual workaround is a shim workflow declaring a same-named job on the inverse
filter, which is a second copy that silently drifts. A job skipped by `if:`
reports as skipped, which satisfies a required check.

**Use `tj-actions/changed-files` or `dorny/paths-filter`.** `AGENTS.md` names the
2025-03 `tj-actions/changed-files` compromise as the reason third-party actions
are pinned to a full commit SHA here. `git diff --name-only` plus a 70-line Node
module needs no dependency at all.

**Test "are any docs changed."** That predicate is true for a pull request that
edits a runbook and rewrites `Program.cs`. It is the specific subtle error #782's
decision 4 names, and `changed-paths.test.mjs` pins it with five mixed cases that
all contain documentation and must all answer `false`.

## What this does NOT cover

The gate never applies on `push` or `workflow_dispatch`, so it changes nothing
about what `main` builds, publishes or attests; `image`'s `image_id` output and
the artifact-handoff proof `publish` does with it (#351) are untouched. It does
not touch `e2e-smoke.yml`, which already path-filters and already skips
documentation pull requests.

**What it does to #146's security gates, stated exactly.** `build-and-test`'s
NuGet `vuln-gate`, `dependency-review`, GitGuardian and CodeQL are untouched and
run on every pull request as before. Two gates DO stop running on a
documentation-only pull request, because they live inside the gated jobs: the two
npm `vuln-gate` audits in `web`, and the Trivy image scan in `image`. Neither can
change verdict because of the diff itself — a documentation-only pull request
changes no `package-lock.json`, no `.csproj`, no `Directory.Packages.props`, no
lock file and no Dockerfile, which by the inverted predicate is guaranteed rather
than hoped for, and those files are the entire input to both. What it does give
up is the incidental re-scan: those two gates also re-run daily-ish against an
UNCHANGED dependency set and would catch an advisory published since the last
pull request, and on a documentation-only pull request they no longer do. The
weekly scheduled `security-audit.yml` is what covers that case, at a coarser
cadence, and it covers it whether or not anyone opens a pull request. This is an
accepted cost, not an absence of one. Restoring it means re-running those two
jobs on a schedule, not gating them differently.

**The `specs/` tree is NOT documentation here**, which is a deliberate departure from
the obvious allow-list. `web/src/routes/helpGlossary.test.ts` reads
`../specs/product/GLOSSARY.md` and fails when a spec term is renamed out from
under the in-app glossary (#657), and that test runs under `npm run
test:coverage` in the `web` job — one of the two jobs this gate skips. A
specs-only pull request would therefore have skipped the guard written for
specs-only pull requests. Carving out that single file instead was rejected:
nothing would notice when a second web test starts reading a second specs path,
and a gate that fails open silently is worse than one that runs four extra
minutes. The cost is that a spec-only pull request runs the full suite. Today
`GLOSSARY.md` is the only path outside `web/` that any web test reads
(`grep -rn 'process.cwd()' web/src` returns one such line), so this is
conservatism about the future rather than a second known consumer.

The `graphify-out/` tree is deliberately NOT documentation here, so a regenerated-graph
pull request runs the full suite. The generated graph is large, machine-written
and not read by a reviewer, which makes it exactly the kind of path where a
wrong "it is only text" judgement would be least likely to be noticed.

Only a ROOT `*.md` counts. `web/README.md` sits inside the web build's own
directory and nothing proves a change there cannot matter to the web job, so it
is code.

The classifier reads paths, not diff content or file status, so a whitespace-only
edit to `Program.cs` still runs everything. That is the intended direction: this
decision buys wall clock on the 17% of pull requests that are unambiguous, not on
the marginal ones.

The gate costs `web` and `image` the `changes` job's own latency (a checkout, a
`git diff` and the changed-path classifier, well under a minute) on every pull
request, because both now wait on it. That is the price of the mechanism and it
is paid on code pull requests too. The classifier's self-test is NOT on that
path: it runs in `classifier-self-test`, which nothing depends on, for the
reason given under "How it is enforced" below.

## How it is enforced

`.github/scripts/changed-paths.test.mjs`, run by `node --test` in the
`classifier-self-test` job on every event — the same self-test pattern the web
job already uses for `vuln-gate` and `lockfix`, moved into its own job for the
reason given under "The rule". Sixteen tests, each watched going red under a
mutation before it was claimed to catch anything: dropping the empty-list check,
dropping the trailing slash from the documentation prefixes, letting any `*.md`
count as root documentation, turning `every` into `some`, accepting a path git
could not hand over verbatim, emptying the exact-filename table, keeping git's
trailing empty line as a path, failing open on an unreadable stdin, losing the
reason it fell back, adding `graphify-out/` to the prefixes, putting `specs/`
back, dropping the non-string guard, hardcoding the CLI's answer to `true`, and
dropping `--no-renames` from `ci.yml`.

**Nothing enforces the `pull_request`-only scope except the classify step's own
first three lines and this record.** No test asserts that a future editor cannot
widen it, because the thing to assert lives in a shell `if` inside a YAML string.
That is the one place a reader should be most careful, and it is why the step
carries the #351 hazard in a comment directly above it rather than a pointer to
one.
