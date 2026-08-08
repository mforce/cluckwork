# Decision records

Each file here holds the **relocated rationale** for a rule that also appears — in
one compressed paragraph — in [`AGENTS.md`](../../AGENTS.md) or
[`README.md`](../../README.md). The short version (the rule + the one-line
consequence of breaking it) stays resident in `AGENTS.md` so it loads into every
agent session; the narrative that earned it (what shipped, which review round
found it, what the wrong fix was, what not to break) lives here so it is reachable
without being resident.

This split is the whole point of [#413](https://github.com/mforce/cluckwork/issues/413):
nearly every long bullet encodes a defect that actually shipped plus the reasoning
that stops it recurring, so the rule is compressed but **nothing is deleted** —
follow the `→` link from the `AGENTS.md` bullet to get here.

## Index

| Decision | Rule lives in |
|---|---|
| [Credential epoch revocation (#364)](364-credential-epoch-revocation.md) | AGENTS · Conventions |
| [Base reference data via guarded raw-SQL migrations (#283)](283-migrations-base-provisioning.md) | AGENTS · Conventions |
| [`InitialCreate` frozen, one migration per change (#407)](407-migration-freeze.md) | AGENTS · Conventions |
| [Seed command and simulation profile (#280, #284, #279)](280-seed-and-simulation.md) | AGENTS · Conventions |
| [First-run admin provisioning: `bootstrap-admin` (#283)](283-first-run-admin-provisioning.md) | AGENTS · Conventions |
| [Migrate command + prod migration split (#263)](263-migrate-command.md) | AGENTS · Conventions |
| [Production Postgres TLS floor + libpq mapping (#261/#262)](261-postgres-tls-floor.md) | AGENTS · Conventions |
| [GSS/Kerberos negotiation off by default (#332)](332-gss-kerberos.md) | AGENTS · Conventions |
| [Container health probe: the `healthcheck` verb (#266)](266-container-health-probe.md) | AGENTS · Conventions |
| [Transient-DB retry, and where it stops (#269)](269-transient-db-retry-boundary.md) | AGENTS · Conventions |
| [A new boot guard must be taught to the sim harness (#370)](370-sim-harness-boot-guards.md) | AGENTS · Conventions |
| [SPA E2E lives in `tools/simulation/ui/` (#277/#385)](277-spa-e2e.md) | AGENTS · Conventions |
| [A write-contract change must update its non-CI callers (#394)](394-write-contract-callers.md) | AGENTS · Conventions |
| [Production logs: compact JSON on stdout (#404)](404-production-logs.md) | AGENTS · Conventions |
| [Writing a guard (a test that asserts an invariant)](407-writing-a-guard.md) | AGENTS · Writing a guard |
| [CI security gates, lock-file healing, Dependabot, action pinning (#146)](146-ci-security-gates.md) | AGENTS · CI security gates |
| [Releases and image publishing — internals (#351)](351-releases.md) | AGENTS · Releases · and `README.md` |
| [Generated PostgreSQL schema documentation (#417)](417-schema-docs.md) | AGENTS · Conventions |

The five short-enough Conventions bullets that kept their full rationale inline in
`AGENTS.md` — break-glass recovery (#265), farm timezone (#264), the proxy-trust
boot guard (#260), the design-time migration connection (#318), and container
image hardening (#267) — have no record here because none of their text was
relocated.
