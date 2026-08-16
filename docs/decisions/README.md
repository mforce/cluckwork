# Decision records

Each file here holds the **relocated rationale** for a rule that also appears — in
one compressed paragraph — in [`AGENTS.md`](../../AGENTS.md), or in a how-to such
as [`docs/releasing.md`](../releasing.md). The short version (the rule + the one-line
consequence of breaking it) stays resident in `AGENTS.md` so it loads into every
agent session; the narrative that earned it (what shipped, which review round
found it, what the wrong fix was, what not to break) lives here so it is reachable
without being resident.

This split is the whole point of [#413](https://github.com/mforce/cluckwork/issues/413):
nearly every long bullet encodes a defect that actually shipped plus the reasoning
that stops it recurring, so the rule is compressed but **nothing is deleted** —
follow the `→` link from the `AGENTS.md` bullet to get here.

Starting a new record: copy [`TEMPLATE.md`](TEMPLATE.md).

## Index

| Decision | Rule lives in |
|---|---|
| [Credential epoch revocation (#364)](364-credential-epoch-revocation.md) | AGENTS · Conventions |
| [Base reference data via guarded raw-SQL migrations (#283)](283-migrations-base-provisioning.md) | AGENTS · Conventions |
| [`InitialCreate` frozen, one migration per change (#407)](407-migration-freeze.md) | AGENTS · Conventions |
| [Seed command and simulation profile (#280, #284, #279)](280-seed-and-simulation.md) | AGENTS · Conventions |
| [First-run admin provisioning: `bootstrap-admin` (#283)](283-first-run-admin-provisioning.md) | AGENTS · Conventions |
| [Migrate command + prod migration split (#263)](263-migrate-command.md) | AGENTS · Conventions |
| [Process role, not statement order (#347)](347-process-role.md) | AGENTS · Conventions |
| [Production Postgres TLS floor + libpq mapping (#261/#262)](261-postgres-tls-floor.md) | AGENTS · Conventions |
| [GSS/Kerberos negotiation off by default (#332)](332-gss-kerberos.md) | AGENTS · Conventions |
| [Container health probe: the `healthcheck` verb (#266)](266-container-health-probe.md) | AGENTS · Conventions |
| [Transient-DB retry, and where it stops (#269)](269-transient-db-retry-boundary.md) | AGENTS · Conventions |
| [A new boot guard must be taught to the sim harness (#370)](370-sim-harness-boot-guards.md) | AGENTS · Conventions |
| [SPA E2E lives in `tools/simulation/ui/` (#277/#385)](277-spa-e2e.md) | AGENTS · Conventions |
| [A write-contract change must update its non-CI callers (#394)](394-write-contract-callers.md) | AGENTS · Conventions |
| [Production logs: compact JSON on stdout (#404)](404-production-logs.md) | AGENTS · Conventions |
| [Generated PostgreSQL schema documentation (#417)](417-schema-docs.md) | AGENTS · Conventions |
| [`AuditEvents` is not time-partitioned (#505)](505-audit-events-no-time-partition.md) | AGENTS · Conventions |
| [Writing a guard (a test that asserts an invariant)](407-writing-a-guard.md) | AGENTS · Writing a guard |
| [CI security gates, lock-file healing, Dependabot, action pinning (#146)](146-ci-security-gates.md) | AGENTS · CI security gates |
| [Releases and image publishing — internals (#351)](351-releases.md) | AGENTS · Releases · and [`docs/releasing.md`](../releasing.md) |
| [Both JWT keys checked at boot, serving-only (#510)](510-jwt-key-boot-check.md) | AGENTS · Conventions |
| [Nothing writes an audit event without an actor (#500)](500-audit-actor.md) | AGENTS · Conventions |
| [Break-glass recovery: `recover-admin` (#265)](265-break-glass-recovery.md) | AGENTS · Conventions · and the [runbook](../runbooks/break-glass-account-recovery.md) |
| [Farm timezone, and the tzdata/ICU constraint (#264)](264-farm-timezone.md) | AGENTS · Conventions |
| [Proxy-trust boot guard (#260)](260-proxy-trust.md) | AGENTS · Conventions |
| [Design-time migration connection, fail-closed (#318)](318-design-time-migration-connection.md) | AGENTS · Conventions |
| [Container image hardening (#267)](267-container-hardening.md) | AGENTS · Conventions |
| [Exactly one serving API instance (#271, #338)](271-single-serving-instance.md) | AGENTS · Host-agnostic repo |

Every Conventions bullet now has a record here. The last seven rows were added
when `AGENTS.md` was compressed to one paragraph per rule: #265, #264, #260,
#318 and #267 had kept their rationale inline until then, and #510, #500 and
#271/#338 had never had a record at all. #260's serving-only *scope* is still
argued in the [#347 record](347-process-role.md), along with #319 and #316.
