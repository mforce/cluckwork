# #508 — diagnosis: same-instant audit events order by a random Guid

**Mode:** bugfix. **Front half:** `bugfix-diagnosis`. **Base commit:** `7f8f31725608e42ac236a52b3bbbcf4cb9b187fc` (`main`).

## D1 — reproduced

Red-capable command, already run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter "FullyQualifiedName~AuditProvenanceTests|FullyQualifiedName~Probe508Tests"
```

A transient probe seeded three events for one flock: a `Flock.Create`, then **two** `Flock.Update`
events sharing one `OccurredAtUtc`, written in a defined order, in separate transactions. The
second-written event was given the **lower** Guid on purpose.

**Captured symptom:**

```
Cluckwork.Api.IntegrationTests.Probe508Tests.Probe_SameInstantChanges_NamesTheLastWritten [FAIL]
  Assert.Equal() Failure: Strings differ
  Expected: "second@farm.test"
  Actual:   "first@farm.test"
```

The other 43 tests in that run passed — `AuditProvenanceTests` is green at the base commit, so this is
a new red, not a pre-existing failure.

The probe is `[DEBUG-508a]`-tagged, was removed before handover, and is **not** the shipped regression
test: the shipped one is authored verbatim in the runbook.

## D2 — root cause

`AuditWriter.WriteAsync` mints `Guid.NewGuid()` — a random v4 — as the audit event's `Id`
(`src/Cluckwork.Infrastructure/Repositories/AuditWriter.cs:54`). `AuditEvents` carries no other
durable ordering column: the table's own columns are `Id`, `AccountId`, `OccurredAtUtc`,
`ActorUserId`, `ActorEmail`, `Action`, `EntityType`, `EntityId`, `Reason`, `DetailsJson`.

Four queries in `AuditEventRepository` therefore break a timestamp tie with a value that carries no
chronology:

| Site | Order clause | Consequence of the arbitrary tiebreak |
|---|---|---|
| `ListAsync` (`:33`) | `OccurredAtUtc DESC, Id DESC` (LINQ) | Arbitrary but **stable** — paging is safe; the Audit page can show a same-instant pair in the wrong order. |
| `GetProvenanceChunkAsync` `created` CTE (`:96`) | `EntityId, OccurredAtUtc ASC, Id ASC` | Cardinality holds only by the **aggregate's** state machine, not by anything the audit layer enforces — see F1 below. |
| `GetProvenanceChunkAsync` `creator` CTE (`:223`) | same | Same as above, and the creator it picks decides which drafting events get suppressed. |
| `GetProvenanceChunkAsync` `latest` (`:241`) | `EntityId, OccurredAtUtc DESC, Id DESC` | **The defect.** `DISTINCT ON` keeps exactly one row per entity, so the tiebreak *selects which actor is displayed*. |
| `GetProvenanceChunkAsync` `promoted` (`:265`) | `EntityId, OccurredAtUtc ASC, Id ASC` | Same unenforced-cardinality argument — see F1. |

The `latest` query is where the arbitrary order becomes a **wrong displayed value** today. The
timestamp shown is correct either way; the name is not.

**This table originally read "harmless" for the other three, on the grounds that at most one row
survives each filter. The D2b review refuted that** — nothing in the schema or the domain type
enforces it, so it is an unenforced invariant doing load-bearing work. All four clauses take the new
tiebreak; see F1.

### Why it is reachable rather than theoretical

The aggregate `Version` concurrency token serialises most concurrent changes to one record: the loser
gets a 409 having written nothing, and audit shares that transaction. `RecordBirdMovementHandler`
escapes the argument — it writes an audit event keyed to the **flock's** id while only inserting a
`BirdMovement` row, so it never bumps `Flock.Version` and never serialises against `Flock.Update`.
Two such requests can land in the same microsecond against the same flock.

`SystemClock.UtcNow` is `DateTime.UtcNow`, and Postgres `timestamptz` stores microseconds, so the
collision window is one microsecond of genuine concurrency.

### Invariants

| ID | Invariant | Enforcement sites (symbols) | Discovered | Source |
|---|---|---|---|---|
| INV-1 | For one entity, "last changed by" names the actor of the newest reportable change. For rows inserted **after this migration**, a tie on `OccurredAtUtc` is broken by insert order — the one written later wins, because `Sequence` is assigned by the database at INSERT. For rows that predate the migration, write order is **not recoverable**: the backfill orders same-timestamp legacy rows by `row_number() OVER (ORDER BY "OccurredAtUtc", "Id")`, and `"Id"` is a random Guid — so a legacy tie is deterministic and stable but arbitrary, exactly as it was before. | `AuditEventRepository.GetProvenanceChunkAsync` (`latest`), and the migration backfill | 2026-09-05 | front-half signoff |
| INV-2 | The ordering key is assigned by the **database** at insert and is never writable by application code — the trail stays append-only with no mutation surface. | `AuditEventConfiguration`, `AuditWriter.WriteAsync`, `AuditEvent` | 2026-09-05 | front-half signoff |
| INV-3 | Each of the three provenance round trips is tenant-scoped by exactly one `AccountId` predicate under `IgnoreQueryFilters`. In the `latest` round trip that predicate lives once in the `scoped` CTE, and `creator`, `shared` and the outer `SELECT DISTINCT ON` all derive from `scoped` rather than restating it — that sharing is deliberate, and the reason is written at the query: a COPIED predicate is what let this triple ship mostly unguarded twice. What must never happen is a round trip with no predicate of its own, or a second independent copy of one. | `AuditEventRepository.GetProvenanceChunkAsync`, `Provenance_LastChange_IsScopedToTheTenant`, `Provenance_MadeOfficial_IsScopedToTheTenant`, `Provenance_EveryQueryIsScopedToTheEntityType` | 2026-09-05 | front-half signoff |
| INV-4 | `ListAsync` returns a deterministic total order, so paging neither skips nor repeats a row. | `AuditEventRepository.ListAsync` | 2026-09-05 | front-half signoff |
| INV-5 | A legacy row's ordering value never contradicts its `OccurredAtUtc`. | the new migration's backfill | 2026-09-05 | front-half signoff |
| INV-6 | `InitialCreate` stays frozen (#407); the change ships as exactly one new migration, with `docs/schema/` regenerated in the same PR (#417). | `src/Cluckwork.Infrastructure/Persistence/Migrations/`, `SchemaDocsTests` | 2026-09-05 | front-half signoff |

## The seam

**A `bigint` identity column `Sequence` on `AuditEvents`, mapped as an EF shadow property, used as the
tiebreak in `ListAsync` and in all four `GetProvenanceChunkAsync` order clauses** (`created`,
`creator`, `latest`, `promoted` — widened from `latest` alone by D2b finding F1).

Fix at the **column**, not at the writer and not at the query alone:

- *Lower* (change `AuditWriter` to mint an ordered id) does not work — see rejected alternatives.
- *Higher* (order in C# after fetching) cannot work: `DISTINCT ON` discards the losing row inside the
  database, so the application never sees it.

**Shadow property, not a domain property.** `AuditEvent` is domain data with deliberately no mutation
surface. A public `Sequence` would be a persistence artifact on a domain aggregate, and it would be
visible to anything that projects the entity. `builder.Property<long>("Sequence")` keeps it a mapped
column that raw SQL orders by and `EF.Property<long>(e, "Sequence")` reaches from LINQ.

### Verified by running it, not asserted

Four probes, all run before this was written: two against a throwaway Postgres 17 container, two by
scaffolding the migration in a throwaway git worktree (both discarded, `[DEBUG-508a/b]`-tagged, and
grepped for afterwards).

1. **What EF scaffolds.** With `builder.Property<long>("Sequence").ValueGeneratedOnAdd()
   .UseIdentityAlwaysColumn()`, `dotnet ef migrations add` produces
   `AddColumn<long>(name: "Sequence", table: "AuditEvents", type: "bigint", nullable: false,
   defaultValue: 0L).Annotation("Npgsql:ValueGenerationStrategy",
   NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)`, plus 6 lines in
   `AppDbContextModelSnapshot.cs`. **A different operation list is a STOP.**
2. **`defaultValue: 0L` is a decoy — it never reaches SQL.** `dotnet ef migrations script` emits
   exactly one statement: `ALTER TABLE "AuditEvents" ADD "Sequence" bigint GENERATED ALWAYS AS
   IDENTITY;`. Do not "fix" the `0L` and do not assume every legacy row lands on `0`.
3. **That statement does backfill existing rows, in *physical* order.** For an append-only heap that
   is never updated — which `AuditEvent` is, by construction: no mutation surface, no update, no
   delete — physical order **is** insert order, so the scaffolded migration is very nearly right.
   What it is not is *guaranteed*: physical order is not a contract, and a `VACUUM FULL`, `CLUSTER`
   or `pg_repack` rewrite may reorder the heap. The probe made this visible by inserting rows whose
   timestamps ran `00:00:02, 00:00:01, 00:00:03` and getting `Seq 1, 2, 3` — insert order faithfully,
   chronological order not at all.
4. **The explicit backfill works end to end** and makes INV-5 hold by construction rather than by
   heap behaviour: add the column nullable, `UPDATE` it from
   `row_number() OVER (ORDER BY "OccurredAtUtc" ASC, "Id" ASC)`, `SET NOT NULL`,
   `ADD GENERATED ALWAYS AS IDENTITY`, then `setval(pg_get_serial_sequence(...), max("Sequence"))`.
   A subsequent insert received `4` after a backfilled `1,2,3`, and a unique index was accepted.

**The choice between 3 and 4 is a real one and belongs to the owner (D3).** Both are correct for the
defect this issue is about, because the tiebreak is only ever consulted *within* one timestamp:

- **Scaffold as generated (3):** one line, no hand-written SQL in the migration. Legacy same-instant
  pairs inherit heap order, which today is their true insert order — arguably higher fidelity than
  anything a backfill can reconstruct. The cost is that `Sequence` is only *incidentally* chronological
  for legacy rows, so a future query that orders by `Sequence` alone has no guarantee.
- **Explicit backfill (4):** ~5 extra lines of hand-written SQL in the `Up`, and `Sequence` then means
  one thing everywhere — its order agrees with `OccurredAtUtc` for every row, old and new. Within a
  legacy same-instant tie the order is arbitrary (that information does not exist in the data and no
  migration can invent it).

Recommendation: **(4)**, because the value's meaning should not depend on heap physics, and because
INV-5 is then provable rather than argued.

### What this fix does and does not buy

It makes the order **total, durable and stable**, and consistent with **insert order**. It does not
make it consistent with *commit* order: identity values are assigned at `INSERT`, so a transaction
that inserts earlier but commits later keeps the lower `Sequence`. That is acceptable and should not
be over-claimed — at a shared microsecond, "which change was really last" is not knowable from the
data, and INV-1 defines the answer as the write order rather than discovering it.

### Rejected alternatives (do not re-propose)

- **UUIDv7 ids** — millisecond granularity with a random tail, so it does not resolve a microsecond
  collision; and every existing row is v4, so a mixed table orders meaninglessly.
- **`ctid`** — physical location, not durable across `VACUUM FULL`.
- **A wider timestamp** — the clock's granularity is not the problem; two genuinely concurrent writes
  can share any timestamp.

## Two deliverables a feature does not have

### 1. Where else does this pattern occur?

`ThenBy(x => x.Id)` over a random Guid is used as a tiebreak at **38 sites across 16 files** — counted
with `grep -rc`, after a first count of "13" produced by a `head`-truncated grep was refuted in the
D2b review. `ExportQueries.cs` alone holds 20; `FlockRepository` 3; `CustomerRepository` 2; the other
13 files one each.

**None of them is this defect, and none is in scope.** Every one is a *list* read where the tiebreak
only has to be deterministic so paging is stable — arbitrary-but-stable satisfies that. The audit
`latest` query is the only site where the tiebreak *selects the single row that is then displayed*,
because `DISTINCT ON` collapses the tie to one row.

The D2b review named the sharpest counter-example, and it is worth recording precisely because it is
*not* a defect: in `BirdMovementRepository` the `Id` tiebreak decides which same-date movement crosses
the `Take(limit)` page boundary, and that row's type, quantity and note are then displayed. That is
page-boundary arbitrariness, not a wrong value — the row shown is a real row with its own real data,
and it is stable across calls. Reported, not fixed, and a follow-up issue is the owner's call.

### 2. Why did the suite not catch this?

Deliberately. `Provenance_WhenTwoEventsShareAnInstant_NamesTheCreatorByAction` is the one same-instant
test, and it was rewritten during PR #503 precisely so it would **not** depend on the tiebreak: it
pins creation by *action*, which is insensitive to the ordering. #503 then recorded — in the code and
in #508 — that no test would pin the arbitrary outcome, because pinning a known loss turns "did not
fix" into "spec".

The gap is therefore real and named: nothing exercised **two same-instant reportable changes**, which
is the only shape that reaches the defect. The probe above is exactly that missing case, and the
shipped regression test is its permanent form.

## Blast radius

- `src/Cluckwork.Domain/Auditing/AuditEvent.cs` — untouched (shadow property).
- `src/Cluckwork.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs` — one shadow
  property. **No new index**: the two existing `HasIndex` calls are untouched, and `Sequence` is only ever
  read as a tiebreak *after* `OccurredAtUtc` has already narrowed the rows.
- `src/Cluckwork.Infrastructure/Repositories/AuditEventRepository.cs` — **five** order clauses
  (`ListAsync`, and `created`, `creator`, `latest`, `promoted` in `GetProvenanceChunkAsync`), and the
  `#508` residual comment replaced by what shipped.
- One new migration + `AppDbContextModelSnapshot`.
- `docs/schema/` regenerated (#417) — **three files**: `public.AuditEvents.md`, `README.md` (its column
  count for this table), and `viewpoint-4.md` (its ERD).
- `tests/Cluckwork.Api.IntegrationTests/AuditProvenanceTests.cs` — **two** regression tests, one per
  surface: the provenance query and the audit list.
- Verified as **not** in scope: the tenant-bypass allowlist is keyed by symbol + file (no query hash),
  and `AuditEvents` has a global query filter so it has no row in `filter-free-set-sites.tsv` — the
  registry only churns if a method **signature** changes, which this fix does not do. The gate is run
  anyway.

## D2b — the diagnosis was reviewed before the owner saw it

Reviewer: **codex** (`codex exec --sandbox read-only`), which did not produce this diagnosis, briefed
to **refute** the mechanism and the seam and told explicitly to ignore prose. Full log retained at
`/tmp/claude-1000/d2b-codex.log`. Five findings; each adjudicated below against the code rather than
accepted on the reviewer's say-so.

**It could not refute the root cause** ("random v4 `Id` is genuinely the deciding key in the failing
`latest` tie"), and it independently confirmed two things the seam rests on: no test and no SPA caller
is pinned to the old Guid tie order (the paging test compares a page against another call using the
*same* ordering, `AuditTests.cs:153`; the SPA preserves server order, `AuditPage.tsx:218,408`), and no
bulk/`COPY` path writes `AuditEvents` — both seeders go through `IAuditWriter`, and the test helper's
direct `AddRange` stays compatible with `ValueGeneratedOnAdd().UseIdentityAlwaysColumn()`.

| # | Finding | Adjudication |
|---|---|---|
| F1 (P2) | "`created`/`creator`/`promoted` are harmless" is unenforced. Nothing constrains `(AccountId, EntityType, EntityId, Action)` to one row, so those three queries also arbitrate a tie by random `Id`, and the *creator* they pick decides which drafting events get suppressed. | **Accepted, and it changes the fix.** In production the cardinality does hold — but by the *aggregate's* state machine (a second `Flock.Create` cannot occur; neither `DailyEntry` nor `SalesOrder` has a path back to Draft), never by anything the audit layer enforces. That is precisely an unenforced invariant used as a justification. Using `Sequence` in **all four** order clauses costs nothing — same column, same clause — and removes the argument entirely. **The seam now covers `created`, `creator`, `latest` and `promoted`.** |
| F2 (P3) | `OccurredAtUtc` is still the primary sort and comes from non-monotonic wall time, so a **clock rollback** can give a later insert an earlier timestamp; `Sequence` is never consulted and the older actor wins. | **Real, and out of scope.** The diagnosis already declined to claim commit-order consistency; this sharpens it to a named residual. Closing it means redefining "latest" as insert order (`ORDER BY "Sequence"` alone), which changes the displayed result for *every* non-tied pair, not just ties — a behaviour change well outside a P2 bugfix. Recorded here; a follow-up issue is the owner's call at D3. |
| F3 (P2) | The `audit-events` export projects an explicit column list and orders by `Id`; it neither exports nor orders by `Sequence`, so a re-import would mint fresh sequence values and could change provenance. | **Downgraded to P3, deferred.** Verified: **no import path exists** — `grep` for an import endpoint returns nothing, and legacy import is unshipped Phase 1.5 scope. The risk is real *for a feature that does not exist yet*, so it belongs to that feature's design, not to this fix. Kept out of scope deliberately: adding `Sequence` to the export would change an existing output contract. |
| F4 (P2) | The backfill is not deployment-ready — a global `row_number()` sort with no supporting `(OccurredAtUtc, Id)` index, on a deliberately unpartitioned table, means a full sort, rewrite, WAL and a non-concurrent DDL lock. | **Accepted as a documentation requirement, not a seam change** — and it does **not** discriminate between the two migration options, which is the part the finding missed: `ALTER TABLE ... ADD COLUMN ... GENERATED ALWAYS AS IDENTITY` **also** rewrites the whole table under `ACCESS EXCLUSIVE`, because it assigns a per-row value. Option (4) adds a sort to a rewrite that was happening anyway. The deploy model already absorbs this: `migrate` is a pre-deploy job and the serving process never runs DDL (#263). The runbook and the PR state the size/lock budget explicitly. |
| F5 (P3) | The sibling inventory is wrong: `ExportQueries` alone has 20 such sites, and `FlockRepository`, `CustomerRepository`, `FeedUsageRepository` were omitted. `BirdMovementRepository`'s tiebreak decides which same-date row crosses a `Take(limit)` boundary and is then displayed. | **Count accepted and corrected — 38 sites across 16 files** (my first count came from a `head`-truncated grep, which is the failure the "walk everything" rule exists to prevent). **Substance deferred:** the `Take(limit)` case is page-boundary arbitrariness, not a wrong value — the displayed row is a real row with its own real data, stable across calls. Out of scope; follow-up is the owner's call. |

**Net effect on the seam:** one change (F1 — all four order clauses, not one), one documentation
requirement (F4 — state the lock and size budget), one corrected fact (F5 — 38, not 13), and two
recorded residuals for the owner to dispose of at D3 (F2 clock rollback, F5 sibling sites).
