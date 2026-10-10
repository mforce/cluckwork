# Decision records

Each file here holds the **relocated rationale** for a rule that also appears — in
one compressed paragraph — in [`AGENTS.md`](../../AGENTS.md) or a scoped `AGENTS.md` it indexes, or in a how-to such
as [`docs/releasing.md`](../releasing.md). The short version (the rule + the one-line
consequence of breaking it) lives in an `AGENTS.md` that agents read before editing the paths it covers; the narrative that earned it (what shipped, which review round
found it, what the wrong fix was, what not to break) lives here so it is reachable
without being resident.

This split is the whole point of [#413](https://github.com/mforce/cluckwork/issues/413):
nearly every long bullet encodes a defect that actually shipped plus the reasoning
that stops it recurring, so the rule is compressed but **nothing is deleted** —
follow the `→` link from the `AGENTS.md` bullet to get here.

There are two kinds of record, and the `AGENTS.md` bullet's link does not by
itself say which: an **earned** record was earned by a defect that shipped (the
narrative names what broke, which review round found it, and the wrong fix), and
an **accepted-risk** record (e.g. the [suspension issuance window (#579)](579-suspension-issuance-window.md))
records a deliberate declination — no incident, the record says `No incident`,
and the rule is load-bearing (break it and the accepted risk returns). Both are
indexed here; the footer below is what distinguishes them from the plain
conventions that carry no link at all.

Starting a new record: copy [`TEMPLATE.md`](TEMPLATE.md).

## Index

| Decision | Rule lives in |
|---|---|
| [Adapter module reach may shrink without a ledger edit (#846)](846-adapter-reach-ratchet.md) | AGENTS · Application shape |
| [Declare adapter tiers before the surfaces that need them exist (#843)](843-adapter-tiers.md) | AGENTS · Application shape |
| [Generate the coupling matrix from live evidence (#848)](848-generated-coupling-matrix.md) | AGENTS · Application shape |
| [Reach a contracted module only through its contract (#849)](849-module-contract.md) | AGENTS · Application shape |
| [Farm settings behind a contract; identity and the account seam outside it (#851)](851-farm-contract.md) | AGENTS · Application shape |
| [Flock lifecycle behind a contract, with read and mortality ports for peers (#852)](852-flock-contract.md) | AGENTS · Application shape |
| [Egg Operations behind a contract, with the egg lot lock order pinned (#853)](853-egg-operations-contract.md) | AGENTS · Application shape |
| [General Inventory behind a contract, keeping the feed-usage lock order (#855)](855-inventory-contract.md) | AGENTS · Application shape |
| [Reach a contracted peer module only through its contract or seam (#1023)](1023-peer-contract-guard.md) | AGENTS · Application shape |
| [Compose every module through its contract, fixture port and registration file (#858)](858-platform-composition.md) | AGENTS · Application shape |
| [Move the architecture and tenant-bypass rules to typed C# registries (#859)](859-typed-rule-registries.md) | AGENTS · Application shape |
| [Table-owner completeness from the EF model (#845)](845-table-owners.md) | AGENTS · Data and correctness |
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
| [Suspension is immediate for use, not issuance (#579)](579-suspension-issuance-window.md) | AGENTS · Conventions |
| [Writing a guard (a test that asserts an invariant)](407-writing-a-guard.md) | AGENTS · Writing a guard |
| [Retire style guards with a named successor (#824)](824-style-guard-conversion.md) | AGENTS · Writing a guard |
| [CI security gates, lock-file healing, Dependabot, action pinning (#146)](146-ci-security-gates.md) | AGENTS · CI security gates |
| [Releases and image publishing — internals (#351)](351-releases.md) | AGENTS · Releases · and [`docs/releasing.md`](../releasing.md) |
| [Both JWT keys checked at boot, serving-only (#510)](510-jwt-key-boot-check.md) | AGENTS · Conventions |
| [Data Protection key ring in Postgres, encrypted in Production (#794)](794-data-protection-key-ring.md) | src/AGENTS · Boot guards |
| [OpenIddict outside Production only, on the shared Data Protection ring (#795)](795-openiddict-server.md) | src/AGENTS · Auth and credentials |
| [OAuth tokens run the session chain, on opted-in endpoints only (#796)](796-oauth-fail-closed.md) | src/AGENTS · Auth and credentials |
| [OAuth client self-registration and the OAuth purge sweep (#797)](797-oauth-client-registration.md) | src/AGENTS · Auth and credentials |
| [Consent with step-up, and the OAuth server in Production (#798)](798-oauth-consent.md) | src/AGENTS · Auth and credentials |
| [Connected apps: listing, Disconnect, last used and audit (#799)](799-connected-apps.md) | src/AGENTS · Auth and credentials |
| [The farm's connected-apps switch refuses; it never revokes (#1146)](1146-connected-apps-switch.md) | src/AGENTS · Auth and credentials |
| [Client ID metadata documents beside registration (#1148)](1148-client-id-metadata-documents.md) | src/AGENTS · Auth and credentials |
| [Nothing writes an audit event without an actor (#500)](500-audit-actor.md) | AGENTS · Conventions |
| [Break-glass recovery: `recover-admin` (#265)](265-break-glass-recovery.md) | AGENTS · Conventions · and the [runbook](../runbooks/break-glass-account-recovery.md) |
| [Farm timezone, and the tzdata/ICU constraint (#264)](264-farm-timezone.md) | AGENTS · Conventions |
| [Proxy-trust boot guard (#260)](260-proxy-trust.md) | AGENTS · Conventions |
| [Design-time migration connection, fail-closed (#318)](318-design-time-migration-connection.md) | AGENTS · Conventions |
| [Container image hardening (#267)](267-container-hardening.md) | AGENTS · Conventions |
| [Exactly one serving API instance (#271, #338)](271-single-serving-instance.md) | AGENTS · Host-agnostic repo |
| [Multi-farm tenancy — several farms on one deployment: shared-DB row-level isolation, farm-code sign-in, per-account email identity, the at-most-one-leader contract, and why an unattributable value cannot be rescued by any rule about when to read it (#530, #586)](530-multi-farm-tenancy.md) | AGENTS · Conventions |
| [Aspire is local orchestration, and it is a second database (#565)](565-aspire-local-orchestration.md) | AGENTS · Build / test / run · Boot guards |
| [A farm code changes only through `rename-account` (#732)](732-farm-code-rename.md) | AGENTS · Conventions · and the [runbook](../runbooks/provisioning-a-new-farm.md) |
| [Backend test coverage measurement, report only (#776)](776-backend-coverage.md) | AGENTS · Build / test / run |
| [Skip the web and image jobs on documentation-only pull requests (#782)](782-ci-job-gating.md) | AGENTS · CI security gates |
| [Require seven CI checks on `main`, tested against the latest `main` (#1182)](1182-required-checks.md) | `.github/AGENTS.md` · Workflow rules |
| [Adopt a UI component library, and which one: MUI (#674)](674-ui-component-library.md) | `web/README.md` · Stack · and `specs/technical/tech_spec.md` §8.1 |
| [Gate two C# style rules at build time (#985)](985-csharp-style-gate.md) | AGENTS · Conventions |
| [Cross-module references are declared in the module ledger (#514, #842)](514-module-ledger.md) | AGENTS · Conventions |
| [No persistence type crosses an Application seam (#514, #847)](847-seam-surface-guard.md) | AGENTS · Conventions |
| [Scale a sales line's list price to its unit, rounded up (#1160)](1160-unit-scaled-list-price.md) | `specs/product/GLOSSARY.md` · Sales line, List price |

**Every bullet that cites an issue has a record here; the plain conventions do
not, and should not.** The Result pattern, handler-per-feature, FluentValidation,
the endpoint shape, `Version++` and nullable-enabled are house
style — consistency rules with no incident behind them and no deliberate
declination to record, so there is nothing to relocate and no `→` link to
follow. A linked bullet is one of two kinds: an **earned** record (a defect that
shipped — the record's narrative names the incident and the review round that
found it) or an **accepted-risk** record (no incident — the record says `No
incident` and the rule is load-bearing, e.g. the suspension issuance window
(#579)). `AGENTS.md` says a bullet is linked by whether it carries one; the
record itself says which of the two linked kinds it is — an accepted-risk record
carries a `No incident` marker (e.g. #579), and an earned record's narrative
names the shipped defect and the review round that found it.

The last eight rows were added when `AGENTS.md` was compressed to one paragraph
per rule: #265, #264, #260, #318 and #267 had kept their rationale inline until
then, and #510, #500 and #271/#338 had never had a record at all. #260's
serving-only *scope* is still argued in the [#347 record](347-process-role.md),
along with #319 and #316.
