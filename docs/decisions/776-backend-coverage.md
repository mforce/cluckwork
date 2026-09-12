# Backend test coverage measurement, report only (#776)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-12
**No incident** — accepted-risk record (no defect shipped; deliberate declination).

## What happened

There was no defect to fix — this is a forward-looking decision, recorded so
the next reader does not re-derive it. The backend (four test projects, 2,584
tests as counted in #776: Domain 491, Application 290, AppHost 10,
Integration 1,793) had never had its coverage measured at all. #776 asked
for the measurement, explicitly **without** a threshold, floor, or gate:
"report only" is the issue's own instruction, and
it is also what `AGENTS.md`'s "Writing a guard" section already argues from
the SPA side — a wrong guard reads as safety, and a floor set before anyone
has seen a number is a guess dressed as one.

`tools/coverage/collect.sh` runs `dotnet test` with coverlet's XPlat Code
Coverage collector across all four projects (`tools/coverage/coverlet.runsettings`),
merges the per-project cobertura output through `reportgenerator` into
`coverage-out/report/<name>` plus a combined report, and writes
`coverage-out/SUMMARY.md` by concatenating each project's generated
`SummaryGithub.md` under a fixed caveat preamble — never by re-parsing a
percentage. `.github/workflows/coverage.yml` runs it on a Monday schedule,
on `workflow_dispatch`, and on a `pull_request` scoped to the tooling's own
inputs.

The first measured run (2026-09-12, this branch) produced:

| Project | Assemblies | Line | Branch | Coverable lines |
|---|---|---|---|---|
| Domain | 1 | 81.4% (1480 of 1817) | 74.9% (651 of 869) | 1817 |
| Application | 3 | 13.9% (1666 of 11901) | 3.6% (114 of 3129) | 11901 |
| Integration | 4 | 93% (15698 of 16866) | 76.4% (3814 of 4989) | 16866 |
| AppHost | 1 | 100% (30 of 30) | 100% (2 of 2) | 30 |
| Combined | 5 | 94% (15890 of 16896) | 79.5% (3968 of 4991) | 16896 |

`Integration`'s run touches four assemblies at very different depths:
`Cluckwork.Api` 92.3% line / 72.8% branch, `Cluckwork.Application` 95.4% /
81.7%, `Cluckwork.Domain` 83.6% / 66.2%, `Cluckwork.Infrastructure` 94.9% /
84.4%.

Generated EF migration code (`src/Cluckwork.Infrastructure/Persistence/Migrations`,
39,088 of the 76,977 lines of C# under `src/`) is excluded from every number above; see
"What this does NOT cover" for what was checked to confirm that.

**Two readings these numbers invite, and both are wrong.** First: Domain
(491 tests) and Application (290 tests) together contribute only 162
coverable lines beyond what Integration already covers on its own — Integration
plus AppHost cover 15,728 of Combined's 15,890 covered lines, over the exact
same 16,896-line denominator. Read naively, that says the 781 unit tests are
almost entirely redundant with the integration suite. **They are not**: this
is the report's own caveat made concrete with a number. A unit test that
asserts a domain invariant and fails with a precise message is not redundant
with an integration test that happens to execute the same line while
asserting an HTTP status code — coverage cannot see that difference, only
mutation testing can, and #771 already found three previously-covered guards
that survived their own mutations. Second: `Application`'s 13.9% line /
3.6% branch reads as "the Application layer is 14% tested", and that
reading is also wrong — `Integration` measures `Cluckwork.Application`
itself at 95.4%. The 13.9% is what the `Application` PROJECT's own 290
tests reach across the three assemblies they incidentally touch (Domain,
Application, Infrastructure), and those tests are predominantly validator
and architectural-guard tests rather than handler exercise; it says
something about what the `Application.Tests` project itself reaches, not
about how tested the `Cluckwork.Application` assembly is overall — for that,
read `Integration`'s 95.4%, with its own EXECUTED-not-ASSERTED caveat
attached.

## The rule

Coverage collection for the four backend test projects is a **measurement
tool, never a gate.** `tools/coverage/collect.sh` produces per-project and
combined reports and a human-readable `coverage-out/SUMMARY.md`; nothing in
CI fails on a number it prints, and adding a threshold is a **separate
decision** that needs its own measured baseline first, the same way the web
floor in `web/vite.config.ts` (#121) was set from actuals after the fact
rather than guessed in advance. `Integration`'s number measures what ran, not
what was asserted, because it drives the API over a real Postgres and
touches nearly every assembly incidentally; read it with that caveat every
time, not just once. Generated EF migration code is excluded from every
number (`ExcludeByFile` in `coverlet.runsettings`) because #407 freezes it
and every integration test executes all of it on container boot regardless
of what that test asserts. Coverage never answers the redundancy question —
whether two tests covering the same lines are both pulling weight, or only
one is. That needs mutation testing, and #776 does not do it.

## Why not the obvious alternative

**Add `--collect` to `ci.yml`'s existing `Test` step.** This is the first
thing a reasonable person reaches for: coverage data already exists once
`dotnet test` runs, so why not just ask for it there? Because `Build and
test` is the critical path of every PR, at roughly 570 seconds today — #775
exists specifically to *shorten* that number, not hold it steady — and
instrumented collection across four projects, one of them a Postgres
integration suite, adds directly to it. A number nobody looks at on every
PR is not worth taxing every PR's merge latency for.

**Set a threshold now, since the tooling is already built.** Also an
obvious reach, and also wrong for a reason `AGENTS.md`'s "Writing a guard"
section already states: **a wrong guard is worse than no guard, because it
reads as safety.** A floor picked before anyone has seen a number is a
guess, not a measurement — it would either sit so low it catches nothing, or
get picked from a single run that happened to be unrepresentative (a flaky
integration test, a coverage tool version quirk) and then block unrelated
PRs on a fluke. The web floor in `web/vite.config.ts` (#121) was set from
measured actuals after the fact and re-baselined repeatedly as real PRs
landed; this follows the same order — measure first, decide whether and
where to floor it as a genuinely separate, later decision.

## What this does NOT cover

This measures backend (.NET) coverage only; `web/vite.config.ts` already has
its own Vitest coverage floor (#121) and is untouched by this decision. This
does not measure or claim anything about test **quality** — a fully covered
line can still guard nothing, as #771 found for three guards that were fully
covered and survived the exact mutations they were named for; that is a
mutation-testing question, not a coverage one, and is out of scope here.
Auto-properties are counted as covered lines (`SkipAutoProps` stays at its
default of `false`), which makes every number in the table above slightly
optimistic; this was left at the default deliberately, to keep the first
measurement free of tuning rather than pre-adjusted before anyone had seen a
baseline to tune against. This does not set, imply, or reserve a future
threshold — a floor is a decision this record explicitly does not make.

## Verified: the migrations exclusion took effect

Checked directly against the first run's output, not assumed from the
`runsettings` config alone. `src/Cluckwork.Infrastructure/Persistence/Migrations`
holds 33 tracked files — 16 timestamped EF migrations, each a `.cs`/`.Designer.cs`
pair (32 files), plus the single `AppDbContextModelSnapshot.cs` — all
EF-generated, none hand-written. Three checks: `coverage-out/report/Integration/SummaryGithub.md`
lists `Cluckwork.Infrastructure` at 94.9% line / 84.4% branch with zero
`*.Migrations.*` classes in its per-class breakdown; the raw collector output
(`coverage-out/raw/Integration/**/coverage.cobertura.xml`) contains zero
`Persistence/Migrations` file paths — the exclusion drops the files before
cobertura ever writes them, not just from the rendered report; and
`Integration`'s reported "Total lines" is 37,465 across all four assemblies
combined — nowhere near the 76,000+ it would be if 39,088 migration lines,
executed to ~100% by every test that boots the container, were folded in.

## How it is enforced

**Nothing enforces the numbers; that is by design, not an oversight** — #776
asked for a measurement, not a gate, and no CI job reads
`coverage-out/SUMMARY.md` or fails on anything in it. What IS enforced: the
`pull_request` path filter on `.github/workflows/coverage.yml` runs the
tooling itself (`tools/coverage/**`, the workflow file, `.config/dotnet-tools.json`,
`tests/Directory.Build.props`, `Directory.Packages.props` and
`tests/**/packages.lock.json`) on every PR that touches those paths, so a
change to the collection mechanism proves itself before it reaches the
Monday schedule — the failure mode `AGENTS.md` records for the sim harness
(#370) and the AppHost (#565): a workflow nobody runs on a normal PR rots
silently. `dotnet restore Cluckwork.sln --locked-mode` in that workflow
still enforces #146's lock-file discipline for the packages this decision
adds (`coverlet.collector`, the `dotnet-reportgenerator-globaltool` manifest
entry).

The last two filter paths were missing from the first revision of this PR and
were added after review. They are not optional: the collector's VERSION is a
coverage-tooling input that lives away from `tools/coverage/`, so without them
a Dependabot bump of `coverlet.collector` changes what collection does and
never runs collection. #684 already required it in general terms — every path
filter names `Directory.Packages.props` explicitly, and a new restore input
goes in all of them — which is the rule the omission broke.
