# The test projects are a fail-fast matrix, not one `dotnet test` (#775)

## What happened

`Build and test` was the critical path of every PR at 6.5 to 11.5 minutes, and
its `Test` step was one `dotnet test Cluckwork.sln` over all four test projects.
Measured on run `34743304133` (PR #815), the job took **9m40s** while every other
job in the run finished far sooner: `Web typecheck, test, and build` 3m38s,
`Image build + Trivy scan` 1m57s, `Dependency review` 11s. All of them start
within 13 seconds of each other, so the jobs were already fully parallel and the
run's wall clock was that one job by itself.

Inside it, the split of work is lopsided. Domain has 491 tests, Application 290
and AppHost 10, and those three take about seven seconds combined. The
integration project has 1,793 and takes 5m26s to 5m54s, because it drives the API
over a real Postgres per collection fixture.

Two costs followed from running them as one step.

**A five-second failure took nine minutes to report.** A broken domain invariant
is knowable almost immediately, but `dotnet test Cluckwork.sln` surfaced it only
when the whole run finished.

**And it kept spending after it already knew.** This is the one that matters, and
splitting into separate *jobs* does not fix it: GitHub does not cancel a sibling
job. Four jobs would each run to completion no matter which went red, so a
domain failure at second five would still leave the integration job burning its
remaining eight minutes of runner time.

## The rule

The four test projects are **legs of one matrix job**, `tests`, with
`fail-fast: true`:

| leg | project | timeout |
|---|---|---|
| `domain` | `tests/Cluckwork.Domain.Tests` | 10 |
| `application` | `tests/Cluckwork.Application.Tests` | 10 |
| `apphost` | `tests/Cluckwork.AppHost.Tests` | 10 |
| `integration` | `tests/Cluckwork.Api.IntegrationTests` | 25 |

`fail-fast` cancels the in-progress legs of a matrix the moment one fails, and it
is the only built-in mechanism that stops that spend. It is GitHub's default and
is written out anyway, because setting it to `false` gives the saving back in one
word while every test still passes and every check stays green.

Each leg restores the solution — `--locked-mode` needs the whole graph — and then
builds only its own project, so the three fast legs never pay for `Api` or
`Infrastructure`. Measured from a cold build: Domain 5s, Application 10s, AppHost
5.5s.

`build-and-test` keeps its id and is renamed **Build, audit and schema docs**. It
runs no tests now. What is left is the work that must happen once per run rather
than once per leg: the blocking NuGet advisory gate (#146) and the schema-docs
freshness check (#417), plus the solution build both reuse — which is also what
enforces warnings-as-errors across every project, including any a test leg's own
graph would not reach.

`publish` declares `needs: [tests, build-and-test, web, image]`. Per #351 a job
that should gate a release belongs in that list, because it is exactly what the
digest artifact proves. Naming the matrix job once covers all of its legs:
`needs` is satisfied only when every leg succeeded, and **a leg cancelled by
`fail-fast` is not a success**, so a cancelled run cannot publish.

## Why not the obvious alternatives

**Separate jobs per project.** Simpler to read and it delivers the earlier
signal, but it delivers none of the cancellation, which is the larger saving. It
was built first and replaced for exactly that reason.

**Folding `web` into the matrix.** It would cancel more, but `web` is 3m38s
against this matrix's ~9m40s worst leg, so it has normally finished before a .NET
failure could cancel anything. It also bundles typecheck, build and two npm audit
gates with its tests, so folding it in means splitting that job in half for a
saving that rarely exists.

**A watcher job calling the cancel-workflow-run API.** Cancels the whole run
including `web` and `image`, but needs `actions: write` on the workflow and is a
hand-rolled mechanism where a built-in one covers the cases that pay.

## What this does NOT do

It does not shorten a **green** run. The integration leg still takes about five
and a half minutes and is still the critical path when everything passes. The
wall-clock levers #775 lists for that case — container reuse and fixture sharing,
and raising xUnit parallelism — are untouched and remain open.

It does not gate the matrix on `changes`. Like `build-and-test`, a job `publish`
needs must never be skipped on a push to `main`, or that commit gets no image and
a release drafted there can never be promoted (#782).

It does not make `fail-fast` free of judgement: a cancelled leg reports as
cancelled, not failed, so read the leg that went red rather than the first
cancellation you see.

## How it is enforced

One `dotnet test Cluckwork.sln` could not leave a project unrun. A matrix **can**,
and it fails silently — a new test project simply never executes while every
check stays green. `SolutionTestProjectSplitTests` closes that:

- `EveryTestProjectInTheSolution_IsNamedByTheWorkflow` walks `Cluckwork.sln` for
  every project under `tests/` and fails unless `ci.yml` names it outside a
  comment. It walks the solution rather than a remembered list, per AGENTS.md's
  guard rules.
- `EveryProjectTheWorkflowNames_Exists` catches the reverse, a leg pointing at a
  path that is not there.
- `TheTestMatrix_KeepsFailFastOn` fails on `fail-fast: false`.

All three were proven red before they were claimed. Dropping the integration leg
reddens the first by name; `fail-fast: false` reddens the third; renaming a leg's
project to one that does not exist reddens the first and second together.

Two traps the guard hit while being written, both worth keeping in mind for the
next file-walking guard here. It first read `dotnet test Cluckwork.sln` out of the
**prose comment** above the job and counted every project as covered — a vacuous
pass, which is why comment lines are stripped before matching. And its path
pattern required a literal `.Tests`, which silently excluded
`Cluckwork.Api.IntegrationTests`; the projects do not agree on that shape.
