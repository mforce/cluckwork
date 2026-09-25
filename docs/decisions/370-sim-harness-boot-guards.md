# A new Production boot guard must be taught to the sim harness (#370)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).

**A new Production boot guard must be taught to the sim harness (#370).** Every guard that fails the boot on missing/invalid config (#260 trusted proxies, #261/#262 the TLS floor, #316 the OTLP endpoint, #319 `AllowedHosts`, and whatever comes next) applies to `tools/simulation/` too — its `app` container runs **Production config on purpose**. **Until 2026-08-08 that harness was deliberately not in CI.** A GitHub job per push was judged out of proportion to 5 seconds of dev-tooling work, so nothing told you when you broke it. Every path into it was human-started, either `reset.sh` directly or `run-baseline.sh`, which calls `reset.sh` once per rep. By 2026-08 four guards had landed without it and it could no longer boot `main` at all. That is the history this rule was written from. The amendment at the end of this file records the trigger today. **When you add or change a boot guard, or add/rename/retire a config key, update `tools/simulation/bootstrap.sh` + `docker-compose.sim.yml` in the same PR** and add the check to `tools/simulation/verify-harness.sh` — a ~0.1s self-check that `reset.sh` runs automatically before it wipes the volume, so a config defect fails in a tenth of a second instead of five minutes later at `/health/ready`. Treat a boot-guard PR that leaves the harness behind like a missing test. Note the harness satisfies guards **properly** (a concrete `AllowedHosts`, the documented plaintext opt-outs for its co-located sidecar) — never by disabling one.

## Amendment, 2026-09-25: the harness is in CI, the k6 sibling is not (#956)

"Deliberately not in CI, so nothing will tell you when you break it" was true
when this was written and is no longer. `.github/workflows/e2e-smoke.yml` runs
`bash tools/simulation/bootstrap.sh` and `bash tools/simulation/verify-harness.sh`
against `docker-compose.sim.yml` on every `pull_request` touching `src/**`,
`web/**`, `tools/simulation/**`, `deploy/**`, `Directory.Build.props`,
`Directory.Packages.props`, `Cluckwork.sln`, `.dockerignore` or that workflow.
Its own header records that owner call, dated 2026-08-08. A boot guard lives under
`src/` and a config key under `deploy/`, so the changes this rule is about now
fail in CI instead of passing silently.

Two gaps keep the rule earning its place. A pull request that touches none of
those paths boots no harness, and `k6-baseline.yml` remains `workflow_dispatch`
only, so nothing runs the load sibling for you. The rest of this record stands.
The harness still runs Production config on purpose, it still satisfies guards
properly rather than by disabling one, and `verify-harness.sh` still fails in a
tenth of a second where `/health/ready` would take five minutes.
