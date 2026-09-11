# A write-contract change must update its non-CI callers (#394)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).

**A change to a write contract must update its non-CI callers in the same PR (#394).** Same failure as #370 with a different trigger: adding a required field, tightening a validator, or changing an aggregate's state machine breaks automated callers of that endpoint that no test references. **Coverage is not uniform, and the gaps do not announce themselves — so work out which layer you changed before trusting any of it.** The **seeders are covered, but only to the handler/domain layer**: `DemoSeedTests`, `SimulationSeederTests`, `SeedCommandTests`, `SimulationSeedCommandTests` and `SimulationCrossDayRerunTests` invoke `DemoDataSeeder`/`SimulationDataSeeder` under `dotnet test`, so a fixture violating a new **handler or aggregate** invariant turns a PR **red**, and `SimulationDataSeeder`'s exact-count validation fails **closed** rather than certifying bad data. **A validator-only tightening is invisible to all five.** The seeders construct commands and call `HandleAsync` **directly** (`DemoDataSeeder` contains not one `ValidateAsync` call) while the endpoint runs `ValidateAsync` *before* the handler — so a fixture payload the real API would now reject keeps every seeder test green. If your change lives in a `*Validator`, the seeders prove nothing; push the rule into the handler/aggregate, or accept that CI is silent here. What is **uncovered outright** is `tools/simulation/k6/` (`k6-baseline.yml`, `workflow_dispatch` only) — and **do not expect a run to surface it either**: `bundles.js`'s `dailyEntryScreen` passes `[403, 409, 422]` as *tolerated*, `authedPost`'s `check()` counts a tolerated status as a **pass**, and `noteIfUnexpected` is handed `expected.concat(tolerated)` — so a new validator answering 422 makes the write workload stop landing entirely against a **100% green baseline**. `reset.sh` never runs k6 at all; it boots and seeds and stops. **So verify the request and status contract by reading it, not by running something.** Grep for callers of the endpoint you changed, check each payload still satisfies the new rule, and narrow the tolerated-status list if a status that used to mean "constrained write" now means "broken write". Read what each caller *does* first: `manager.spec.ts` records **and submits**, while `worker.spec.ts` deliberately **saves a draft and never submits** (its rerunnability contract depends on that), so a submit-contract change must not "fix" the worker spec into submitting. Treat a write-contract PR that leaves the uncovered callers behind like a missing test.

## Amendment, 2026-09-11: the Playwright half stopped being true

This rule was written when **both** `tools/simulation/k6/` and the Playwright specs in
`tools/simulation/ui/` were dispatch-only, and the paragraph above said so. Half of that is no longer
the case, and the half that changed is the half a reader is most likely to act on.

`e2e-smoke.yml` runs the quick suite **on `pull_request`** — an owner call dated 2026-08-08, recorded
in that workflow's own header — path-filtered to `src/**`, `web/**`, `tools/simulation/**`,
`deploy/**`, the three root image-build inputs and the workflow file. A write-contract change lives in
`src/` by definition, so **the Playwright specs do run for exactly the class of change this rule
governs**. Roughly 28 specs across the 14 files, in about three minutes. Only `slow` (the real
15-minute token-expiry spec) and `canary` remain dispatch-only, and a docs-only PR skips the job.

`k6-baseline.yml` is still `workflow_dispatch` only. Everything the paragraph above says about the
tolerated-status list hiding a broken write is unchanged and still the sharpest trap here.

**Why this is worth correcting rather than leaving.** The error pointed the safe way — it made you
verify by hand something CI already covered — so nothing shipped broken because of it. The cost was
misdirected effort: on #727 the Playwright half of the caller read was done by hand under the belief
that a green baseline would hide a break, when CI would have caught it. It also flattened the one
caller that genuinely still needs reading into a list of two, which makes it easier to skip.

It is a trap in the other direction too. Anyone who checks `e2e-smoke.yml`, sees Playwright covered,
and concludes the whole bullet is stale will stop reading the k6 callers as well.

**The durable form of the rule is the reading, not the coverage claim.** CI can tell you a spec broke.
It cannot tell you a caller's *intent* changed — which is why `manager.spec.ts` records and submits
while `worker.spec.ts` deliberately never submits, and why a submit-contract change must not "fix"
the worker spec into submitting. That part of this decision is untouched.

Found while shipping #727; filed as #767.
