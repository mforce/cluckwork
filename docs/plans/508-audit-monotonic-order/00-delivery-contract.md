# Delivery contract — #508

## 1. Identity

- **Slice:** #508 — `fix(api): order same-instant audit events by a durable monotonic key, not a random Guid`. Give `AuditEvents` a database-assigned monotonic ordering column and use it as the tiebreak, so a same-instant pair of reportable changes names the right actor as the last changer.
- **Mode:** bugfix
- **Front half that produced this:** `bugfix-diagnosis`, signed off 2026-09-05 (D3 record in §2)
- **Repo + default branch:** `/home/mforce/dev/cluckwork`, `main`

## 2. Approved scope

- **Signoff artifact:** `docs/plans/508-audit-monotonic-order/01-diagnosis.md`
- **⛔ Signoff record:** Owner (`mforce`), 2026-09-05, on `01-diagnosis.md` as of this commit — the version carrying the D2b adjudication table (F1 accepted, widening the seam from one order clause to all five). Given in chat rather than through `AskUserQuestion`: the owner declined the question form as too dense, asked for a plain-English restatement, and then answered **"Pick B"** — approving the root cause and the seam, and choosing the **explicit chronological backfill** (option 4 in the diagnosis) over the scaffolded one-liner.
- **Acceptance criteria:**
  - Two reportable changes to one entity sharing an `OccurredAtUtc` name the **later-written** actor as the last changer (INV-1).
  - The ordering value is assigned by Postgres at insert and is not writable from application code (INV-2).
  - Each of the three provenance round trips keeps exactly one `AccountId` predicate under `IgnoreQueryFilters`; in the `latest` round trip the CTEs derive from the single `scoped` predicate rather than restating it (INV-3, as corrected).
  - `ListAsync` still returns a deterministic total order; paging neither skips nor repeats (INV-4).
  - Legacy rows carry an ordering value that does not contradict their `OccurredAtUtc` (INV-5).
  - Exactly one new migration; `InitialCreate` untouched; `docs/schema/` regenerated in the same PR; `dotnet ef migrations has-pending-model-changes` clean (INV-6).
  - `dotnet build Cluckwork.sln` clean (warnings are errors) and `dotnet test Cluckwork.sln` green.
- **Explicitly out of scope:**
  - The 38 sibling `ThenBy(x => x.Id)` paging tiebreaks (across 16 files; an earlier count of "13" came from a truncated grep), and the `audit-events` export's `OrderBy(OccurredAtUtc).ThenBy(Id)` — reported in the diagnosis, not fixed here; changing the export's order would change an existing output contract.
  - Changing `AuditWriter`'s `Guid.NewGuid()` id, or the `Id` primary key.
  - Any change to what the SPA renders.
- **Simplicity ceiling:** *One EF shadow property backed by one `bigint` identity column, replacing `Id` as the tiebreak in all five order clauses that carry one (`ListAsync`, and `created`/`creator`/`latest`/`promoted` in `GetProvenanceChunkAsync`).* Complexity budget: **7 files** — `AuditEventConfiguration.cs`, `AuditEventRepository.cs`, one new migration, `AppDbContextModelSnapshot.cs`, `docs/schema/public.AuditEvents.md`, `AuditProvenanceTests.cs`, and the issue/PR text. New concepts allowed: **none**. Non-goals: no domain-model change to `AuditEvent`, no new repository method, no new endpoint, no second migration.
- **Implementation plan:** `docs/plans/508-audit-monotonic-order/02-implementer-runbook.md`

## 3. Invariants

| ID | Invariant | Enforcement sites (symbols) | Discovered | Source |
|---|---|---|---|---|
| INV-1 | For one entity, "last changed by" names the actor of the newest reportable change. For rows inserted **after this migration**, a tie on `OccurredAtUtc` is broken by insert order — the one written later wins, because `Sequence` is assigned by the database at INSERT. For rows predating the migration, write order is **not recoverable**: the backfill orders same-timestamp legacy rows by `row_number() OVER (ORDER BY "OccurredAtUtc", "Id")` over a random Guid, so a legacy tie is deterministic and stable but arbitrary — as it was before. **Corrected 2026-09-05 on the owner's ruling**, same round-2 finding class as INV-3. | `AuditEventRepository.GetProvenanceChunkAsync` (`latest`), and the migration backfill | 2026-09-05 | front-half signoff; INV-1 text corrected 2026-09-05 by owner ruling at the Phase 13 ask |
| INV-2 | The ordering key is assigned by the database at insert and is never writable by application code; the trail keeps no mutation surface. | `AuditEventConfiguration`, `AuditWriter.WriteAsync`, `AuditEvent` | 2026-09-05 | front-half signoff |
| INV-3 | Each of the three provenance round trips is tenant-scoped by exactly one `AccountId` predicate under `IgnoreQueryFilters`. In the `latest` round trip that predicate lives once in the `scoped` CTE, and `creator`, `shared` and the outer `SELECT DISTINCT ON` all derive from `scoped` rather than restating it — that sharing is deliberate, and the reason is written at the query: a COPIED predicate is what let this triple ship mostly unguarded twice. What must never happen is a round trip with no predicate of its own, or a second independent copy of one. **Corrected 2026-09-05 on the owner's ruling** — the original wording ("never shared or copied between CTEs") asserted the opposite of what ships and was caught by review round 2. | `GetProvenanceChunkAsync`, `Provenance_LastChange_IsScopedToTheTenant`, `Provenance_MadeOfficial_IsScopedToTheTenant`, `Provenance_EveryQueryIsScopedToTheEntityType` | 2026-09-05 | front-half signoff; INV-3 text corrected 2026-09-05 by owner ruling at the Phase 13 ask |
| INV-4 | `ListAsync` returns a deterministic total order; paging neither skips nor repeats a row. | `AuditEventRepository.ListAsync` | 2026-09-05 | front-half signoff |
| INV-5 | A legacy row's ordering value never contradicts its `OccurredAtUtc`. | the new migration's backfill | 2026-09-05 | front-half signoff |
| INV-6 | `InitialCreate` stays frozen (#407); one new migration; `docs/schema/` regenerated in the same PR (#417). | `Persistence/Migrations/`, `SchemaDocsTests` | 2026-09-05 | front-half signoff |

## 4. Ownership map

| Surface | Owning slice | Lands first | Forward-compat carried by |
|---|---|---|---|
| `AuditEvents` schema + `AuditEventRepository` ordering | **#508 (this slice)** | this slice | n/a — no sibling slice is in flight against this surface, verified against the open-issue list |
| Sibling repository paging tiebreaks (38 sites across 16 files) | none — reported, unowned | n/a — nothing lands, no slice claims them | n/a — not a defect; arbitrary-but-stable satisfies paging |
| `audit-events` export row order | none — reported, unowned | n/a — nothing lands, deliberately left alone | n/a — changing it would change an existing output contract |

## 5. Baseline

- **Base commit:** 7f8f31725608e42ac236a52b3bbbcf4cb9b187fc
- **Baseline result:** `dotnet test Cluckwork.sln --configuration Release`, run by the driver on `7f8f3172`, exit 0:
  - `Cluckwork.Domain.Tests` — `Failed: 0, Passed: 365, Skipped: 0`
  - `Cluckwork.AppHost.Tests` — `Failed: 0, Passed: 10, Skipped: 0`
  - `Cluckwork.Application.Tests` — `Failed: 0, Passed: 241, Skipped: 0`
  - `Cluckwork.Api.IntegrationTests` — `Failed: 0, Passed: 1686, Skipped: 0`
  - **2302 total.** Separately, the `AuditProvenanceTests` slice alone is `Passed: 43, Failed: 0` (the 44th test in the earlier D1 run was the transient `[DEBUG-508a]` probe, which failed by design and has been removed).
- **Already failing at baseline:** none — **driver-verified**, not CI-attested. No [AUTH-4] substitution is claimed for any gate: the driver ran build, the full suite, and the locked restore itself.

## 6. Agents (the Phase 0 answers)

- **Risk class and review budget:** **high** — driver-classified at Phase 0, no front half had set one. Blast radius is small but the class is not: a **schema migration** on the **audit trail** (data-retention surface), **three raw-SQL reads that opt out of the tenant query filter** (`IgnoreQueryFilters` — AGENTS.md records this exact scoping triple shipping mostly unguarded twice), and a defect whose reach argument is **concurrency**. That buys the full four-seat roster below and a per-seat bounded brief.
- **Implementer:** Claude sonnet, dispatched as its own `bb thread` — `bb thread spawn --new-environment worktree --parent-self --model <sonnet> --permission-mode auto`. Rationale: spec cost vs diff cost favours delegation here, but only to a tier that can *infer* — the work needs `dotnet ef migrations add` against a live toolchain, a hand-edited backfill in the generated `Up`, `tools/schema-docs/generate.sh` (which starts containers), and a red-first test. A transcriber tier drifts on exactly those; the verbatim-spec parts (the SQL, the test) are authored by the driver anyway.
- **Reviewers:** four seats, each named for the defect class it hunts. (a) **codex** — SQL and migration correctness: tiebreak direction, `DISTINCT ON`/`ORDER BY` agreement, identity-column semantics, backfill cost and ordering. (b) **Claude agent, repo-rules seat** — the repo's own written rules: #407 migration freeze, #417 schema-docs regeneration, #370 sim harness, #632 registry keying, #394 non-CI callers, the guard-writing rules. (c) **Claude agent, tenant-isolation seat** — the three `IgnoreQueryFilters` predicates and their mutation guards. (d) **pi contrarian** — `pi --provider nous-portal --model qwen/qwen3.8-flash`, briefed to refute; non-load-bearing by standing instruction.
- **Reviewer brief shape:** one defect class per seat, named files only, **max 6 findings**, ~10 minutes, and a **merge-or-defer verdict per finding**. Each brief states that inflating a P3 to a P1 costs a review round an issue would have carried, and that repository exploration without a new hypothesis is token waste rather than diligence. The repo-rules seat's brief is deliberately *not* written from my suspicions — it is handed the rule set and the diff and asked which rules the diff touches.
- **Seat briefed on the repo's own written rules:** the Claude repo-rules seat; instruction files `AGENTS.md`, `CLAUDE.md`, and the `docs/decisions/` records the diff's rules link to (#407, #417, #283, #318, #632).
- **Driver fix budget:** **0 (default)** — shipped code only. Transient verification edits (mutation checks, probes) remain available and have already been used at D1/D2.
- **Background visibility:** the implementer runs as its own **bb thread**, watched live in the BB IDE panel; the driver additionally polls `bb thread show`/`log` on its own turn boundaries.
- **Approval-prompt route:** the bb thread's own UI — a blocked approval prompt appears in that thread for the owner to answer directly. The driver checks `bb thread show` at each turn boundary and reports a stall rather than re-dispatching on suspicion.
- **Repo conventions, restated:** Layering is `Api → Application/Infrastructure → Domain`, and **Domain depends on nothing**; do not add a persistence artifact to a domain type. Handlers return `Result`/`Result<T>` and throw only on invariant violations; no MediatR. **Nullable is enabled, unused usings are build-breaking, and warnings are errors.** Every aggregate mutation bumps `Version` (not applicable here — `AuditEvent` has no mutation surface, by design). Every tenant-owned entity carries a non-nullable `Guid AccountId` which is both a global-query-filter key and an EF concurrency token; never map it as anything else. **`InitialCreate` is frozen: one migration per change, never folded in, and base reference data ships as guarded raw SQL, never `HasData`.** `docs/schema/` is generated by `tools/schema-docs/generate.sh` and CI fails a stale PR. Tests: integration tests use real Postgres via Testcontainers (Docker required), never SQLite. A guard is mutation-checked before its claim is written. `main` is protected — branch, push, open a PR, never commit to `main`; the **PR title is the release note** and must be a conventional-commit subject.
- **Lessons file:** `/home/mforce/.bb/thread-storage/thr_nq5h6jk6kd/508-lessons.md` — outside the repo, outside every reviewer read root.

## 7. Merge authority

- **Who merges:** the owner — this is the ⛔ Phase 13 gate, and the answer is never the driver.
- **Standing authorisation, if any:** none — this is an ordinary bugfix slice, not an incident.
- **Disclosure constraints:** none — not a security remediation; the defect is public in issue #508 already.
- **Follow-up and removal condition:** n/a — not an incident, and nothing here is a mitigation with a removal condition.
