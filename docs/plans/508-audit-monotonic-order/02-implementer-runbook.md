# Runbook — #508: order same-instant audit events by a durable monotonic key, not a random Guid

You are an autonomous coding agent with FULL tools (read, edit, write, bash) in the `cluckwork` repo
(.NET 10 / C#, Postgres via EF Core, cwd = repo root). Execute this runbook top to bottom. You do
EVERYTHING: branch, edit, build, test, commit, open the PR.

**Context, in one paragraph.** Every screen that shows "last changed by X" reads it out of the
`AuditEvents` table. To find the newest event the query sorts by `OccurredAtUtc` and breaks ties with
`Id` — but `AuditWriter` mints `Id` as a random v4 `Guid`, so the tie is decided arbitrarily and the
**wrong actor can be named**. The fix is a database-assigned `bigint` identity column, `Sequence`, used
as the tiebreak instead. Full diagnosis: `docs/plans/508-audit-monotonic-order/01-diagnosis.md`.

## Rules

- Transcribe the exact code blocks VERBATIM (comments and whitespace included). Do not reformat, rename,
  or "improve" them. Blocks marked **PROTECTED** are correctness-critical: transcribe or stop, never
  repair.
- Run the commands EXACTLY as given. Do not invent flags.
- After every build/test command, if it is not clean, STOP and fix before continuing. **An expected RED
  is a clean result** — but only that exact RED: the command as written, the named test, failing at the
  named assertion. Anything else is a STOP, however red it looks: a compile error, a discovery or runner
  failure, zero tests collected, a different test failing, or a baseline failure that has changed shape.
- **Every gate command cites its gate row by ID** (G1..G4) and is never retyped. A filtered invocation
  says which row it narrows.
- Do NOT touch:
  - `src/Cluckwork.Domain/Auditing/AuditEvent.cs` — the ordering key is a persistence concern and is
    mapped as an EF **shadow property**; the domain type deliberately does not learn about it.
  - `src/Cluckwork.Infrastructure/Repositories/AuditWriter.cs` — the random `Guid` id stays; it is the
    primary key, and changing it is explicitly out of scope (see the diagnosis's rejected alternatives).
  - `src/Cluckwork.Infrastructure/Persistence/Migrations/20260801190854_InitialCreate.cs` — frozen (#407).
  - `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs` — the `audit-events` export's row order
    is an existing output contract and is out of scope.
  - Any other repository's `ThenBy(x => x.Id)` — 38 such sites exist; none is this defect.
- Files you may create or edit:
  - `src/Cluckwork.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs`
  - `src/Cluckwork.Infrastructure/Repositories/AuditEventRepository.cs`
  - `src/Cluckwork.Infrastructure/Persistence/Migrations/` (the ONE new migration + `AppDbContextModelSnapshot.cs`)
  - `docs/schema/` — **generated, never hand-edited.** `generate.sh` runs `tbls doc --rm-dist` over the
    whole directory, and **three** files change: `public.AuditEvents.md` (the new column and its NOT NULL
    constraint), `README.md` (its table lists AuditEvents' column count, `10` → `11`), and
    `viewpoint-4.md` (renders the column list in its ERD). Committing only the first fails G3.
  - `tests/Cluckwork.Api.IntegrationTests/AuditProvenanceTests.cs`
  - `docs/plans/508-audit-monotonic-order/` — the diagnosis and this runbook, copied in and committed
    (see increment 3). They do not exist on the pinned base commit; read them from the absolute paths
    given in your dispatch.
  - `/tmp/cluckwork-508-pr-body.md` — outside the repo, for the PR body

  Anything else, STOP and report.
- Work only on the new branch. Never commit to `main`.
- A **mutation check** means: plant a bug on purpose, run the suite, and see whether a test notices. RED
  means that test guards the code; GREEN means nothing was watching. Then restore, **rebuild**, and
  re-run to confirm you are clean.
- Run the FULL test suite — **G2 exactly as its row records it** — in the FOREGROUND and report its final
  summary line verbatim. Do NOT background it.
- If a code block here conflicts with an existing test, STOP and report the conflict. Do NOT relax or
  delete that test.
- **PROTECTED blocks are never edited, for any reason.** If one fails to compile or fails at runtime,
  STOP and report the exact error.
- Any OTHER block can fail three ways: (1) does not compile → fix minimally, report the error and your
  fix; (2) compiles but is wrong in itself → same, and report it prominently as a runbook defect;
  (3) an assertion fails against the product → report the RED, NEVER widen or relax the assertion.

**Protected-block probe — what the driver ran before dispatch:**

| Claim the PROTECTED blocks rest on | How it was settled |
|---|---|
| What `dotnet ef migrations add` scaffolds for this shadow property | Scaffolded it in a throwaway worktree: `AddColumn<long>(name: "Sequence", table: "AuditEvents", type: "bigint", nullable: false, defaultValue: 0L).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)` + 6 lines in the snapshot. **A different operation list is a STOP.** |
| Whether `defaultValue: 0L` reaches the database | `dotnet ef migrations script` — it does **not**. The emitted SQL is one statement: `ALTER TABLE "AuditEvents" ADD "Sequence" bigint GENERATED ALWAYS AS IDENTITY;`. This is why the `Up` below is hand-written instead. |
| Whether the hand-written backfill works on a **populated** table | Run against a throwaway Postgres container on the same major version the schema-docs generator pins: three rows stamped `00:00:02, 00:00:01, 00:00:03` came back `Sequence` 1,2,3 in **timestamp** order, and the next insert got 4. (The image tag is deliberately not written out here — `SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` requires one identical digest-pinned reference across every **tracked** file, and this runbook becomes tracked at increment 3.) |
| Whether it works on an **empty** table — the case every Testcontainers run hits | Run: `UPDATE 0`, `setval(..., 0 + 1, false)`, and the first two inserts got `Sequence` 1 and 2. The `COALESCE(..., 0) + 1, false` form is load-bearing; `COALESCE(max, 1)` would start a fresh database at 2. |
| Whether `pg_get_serial_sequence` resolves a **mixed-case** column name | Run: `pg_get_serial_sequence('"AuditEvents"', 'Sequence')` resolves. The quoting on both arguments is load-bearing. |
| Whether `GENERATED ALWAYS` actually refuses an application-supplied value (INV-2) | Run: `ERROR: cannot insert a non-DEFAULT value into column "Sequence" / DETAIL: Column "Sequence" is an identity column defined as GENERATED ALWAYS.` The database enforces INV-2; no application-side guard is needed. |

**Existing instances of this pattern:** *novel — no existing instance.* No other entity in this repo
carries a database-generated ordering column: every `Id` is an application-minted `Guid`, and
`grep -rn "UseIdentityAlwaysColumn\|ValueGeneratedOnAdd" src/` returns nothing before this change. The
`migrationBuilder.Sql` **form** is not novel — the base-reference migrations (#283) use it — but this is
the first use of it for a schema backfill rather than for seed data. That is the claim; if you find a
prior instance, say so.

## Verify prerequisites (run first)

```bash
git rev-parse --abbrev-ref HEAD    # expect: main
git status --short                 # expect: only untracked docs/plans/508-audit-monotonic-order/, which is this runbook and its diagnosis
git rev-parse HEAD                 # expect: 7f8f31725608e42ac236a52b3bbbcf4cb9b187fc
dotnet --version                   # expect: 10.0.302 — observed on the driver host, informational, does not gate
docker info --format '{{.ServerVersion}}'   # expect: a version string, not an error — integration tests need Docker
git config core.hooksPath          # expect: .githooks, or empty if the hook is not enabled
```

Then run **G4** once, before anything else builds:

```bash
dotnet restore Cluckwork.sln --locked-mode
```

**G1 carries `--no-restore`**, so it fails against an unrestored tree — which is what a fresh worktree is.
Restore once here and once more in the final verification phase.

**The commit gate.** `.githooks/pre-commit` runs `dotnet test tests/Cluckwork.Domain.Tests` and
`dotnet test tests/Cluckwork.Application.Tests` when any `.cs`/`.csproj`/`.sln`/`Directory.*.props` file
is staged. Read from the hook file, not from memory: it checks the **working tree**, not the staged
snapshot, and it is bypassable with `--no-verify` or `SKIP_HOOKS=1`. **Do not bypass it.** Because it
builds the tree, **no increment below may commit a non-compiling tree** — which is why the configuration,
the migration and the query changes are one commit.

## Caller ledger

| Increment | Contract changed | Every production caller | What each does AT THIS COMMIT | Same-commit or later? | Observed at that commit (Phase 11) |
|---|---|---|---|---|---|
| 1 | none — no public signature changes. `Sequence` is a shadow property; `IAuditEventRepository.ListAsync` and `GetProvenanceAsync` keep their signatures exactly. | `AuditEndpoints.cs:39` (`ListAsync`); the five provenance callers reached through `GetProvenanceAsync`; `AuditWriter.WriteAsync` (writes, does not order); `DemoDataSeeder` and `SimulationDataSeeder` (write through `IAuditWriter`); `ExportQueries.cs:256` (reads `AuditEvents`, projects an explicit column list that does not include `Sequence`) | All compile and behave unchanged. The seeders write through `IAuditWriter`, so the database assigns `Sequence` on insert with no code change. The export names its columns explicitly, so a new shadow property cannot leak into it. | same commit | *(driver fills at Phase 11)* |
| 2 | none — generated artifact only | `tools/schema-docs/generate.sh --check` in CI | Fails until `docs/schema/public.AuditEvents.md` is regenerated | same commit as the regeneration | *(driver fills at Phase 11)* |

## Driver-observed expectations

| Expectation | Observed by | On | Gates which step |
|---|---|---|---|
| `AuditProvenanceTests` has 43 `[Fact]`s and all 43 pass on the base commit | `dotnet test ... --filter "FullyQualifiedName~AuditProvenanceTests"` | driver host, base commit | Step 1a — a different pass count before your edits is a STOP |
| The scaffolded migration's operation list (see the probe table) | `dotnet ef migrations add` in a throwaway worktree | driver host, throwaway worktree | Step 1c — a different operation list is a STOP |
| `docs/schema/` contains `public.AuditEvents.md`, and `README.md` records AuditEvents as **10** columns | `ls docs/schema`, `sed -n 37p docs/schema/README.md` | driver host, base commit | Increment 2 — after regeneration that count must read 11 |
| **The whole runbook applies and goes green.** The driver applied increments 1b–1d and 1a in a throwaway worktree off `7f8f3172` and ran it end to end. | `dotnet build Cluckwork.sln --configuration Release --no-restore` → `0 Warning(s), 0 Error(s)`; then `dotnet test ... --filter "FullyQualifiedName~AuditProvenanceTests"` → `Failed: 0, Passed: 45` | driver host, throwaway worktree (discarded) | Step 1f — **45 passed is the number to hit.** Fewer, or any build warning, is a STOP |
| The scaffolded `Up` is exactly one `AddColumn<long>` with the `IdentityAlwaysColumn` annotation | `dotnet ef migrations add AddAuditEventSequence` in that worktree | driver host, throwaway worktree | Step 1c — a different operation list is a STOP |
| **No extra `using` is needed.** `UseIdentityAlwaysColumn` resolves from the already-imported `Microsoft.EntityFrameworkCore`. | the clean build above, with no `using` added | driver host, throwaway worktree | nothing — if the build demands one, add it and report the mismatch |
| Model and migrations agree after the hand-written `Up` | `dotnet ef migrations has-pending-model-changes` → `No changes have been made to the model since the last migration.` | driver host, throwaway worktree | Final verification — a pending-changes report is a STOP |
| **Mutation row 1 behaves as predicted.** Reverting the `latest` tiebreak to `e."Id" DESC` reddens exactly one test. | mutate → rebuild → `dotnet test ... --filter "FullyQualifiedName~AuditProvenanceTests"` → `Failed: 1, Passed: 44`, failing at `Expected: "second@farm.test" / Actual: "first@farm.test"` | driver host, throwaway worktree | Mutation row 1 — **only one test may fail.** If several redden, the mutant is not isolated and the row proves less than it claims |

## Gate commands

Every command below is **copied** from `.github/workflows/ci.yml`, job `build-and-test`.

| ID | Gate | Source | Command, verbatim | Baseline on `7f8f3172` | Clean looks like |
|---|---|---|---|---|---|
| G1 | build | `.github/workflows/ci.yml`, `build-and-test` → step `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean — driver-verified (warnings are errors, so any warning is a failure) | `Build succeeded.` with `0 Warning(s)` and `0 Error(s)` |
| G2 | test | `.github/workflows/ci.yml`, `build-and-test` → step `Test` | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | **clean, driver-verified on `7f8f3172`** — `Domain.Tests 365`, `AppHost.Tests 10`, `Application.Tests 241`, `Api.IntegrationTests 1686`; **2302 total, Failed: 0, Skipped: 0**. Nothing already red. | four `Passed!` lines, each `Failed: 0`, totalling **2304** — the baseline plus the two tests this slice adds. A total below 2302 is a STOP even if `Failed: 0`, because it means tests stopped being discovered |
| G3 | schema docs | `.github/workflows/ci.yml`, `build-and-test` → step `Verify schema docs are current` | `tools/schema-docs/generate.sh --check` | clean on the base commit — driver-verified by inspection of the committed docs; **the script starts containers, so it needs Docker** | exits 0 with no diff |
| G4 | restore | `.github/workflows/ci.yml`, `build-and-test` → step `Restore dependencies` | `dotnet restore Cluckwork.sln --locked-mode` | clean — driver-verified in a throwaway worktree | `Restored ...` for every project, no `NU1004` |

**No gate here is ratcheted** — there is no coverage threshold on the .NET side, so no trip response is
needed. (The SPA coverage ratchet exists but this slice touches no `web/` file.)

## Documentation surfaces

| Surface | Path / key | Locales | Increment | Verification procedure (RUN at Phase 11) | Verified by + SHA |
|---|---|---|---|---|---|
| Generated schema docs | `docs/schema/public.AuditEvents.md` | n/a — generated English artifact, not a user-facing string | 2 | `tools/schema-docs/generate.sh --check` exits 0, and the committed file lists the `Sequence` column | *(driver, Phase 11)* |
| GLOSSARY / in-app Help / SPA | — | — | none | **none — and why:** this change alters no user-visible text, no screen, no API response field and no export column. It changes only *which of two same-instant rows* is selected. The repo's "keep documentation in sync" rule is scoped to user-visible behaviour; the behaviour change here is a correction to an existing field's value, not a new concept. **If you disagree, STOP and say so** — this is a judgement call the driver made, not a fact. | n/a |
| Code comment carrying the old residual | `AuditEventRepository.cs`, the `KNOWN RESIDUAL, accepted rather than overlooked (#508)` block | n/a | 1 | The block is replaced by what shipped — increment 1e | *(driver, Phase 11)* |

## Step 0 — branch

Branch from the **pinned SHA**, not from whatever `main` has become. Every exact-block claim and the
2302-test baseline in this runbook were verified against `7f8f3172`; if `main` has advanced, they describe
a different tree.

```bash
git checkout -b fix/508-audit-monotonic-order 7f8f31725608e42ac236a52b3bbbcf4cb9b187fc
git rev-parse HEAD    # expect: 7f8f31725608e42ac236a52b3bbbcf4cb9b187fc
```

**Do not `git pull` first.** If you believe the branch must be rebased onto a newer `main`, STOP and
report — that is a driver decision, and it invalidates the prerequisites you just checked.

===================================================================================
# INCREMENT 1 — a durable monotonic tiebreak for `AuditEvents`
===================================================================================

RED → GREEN. Do not reorder: the failing run is the proof the tests can fail at all.

## 1a. RED — add the two failing tests

Edit `tests/Cluckwork.Api.IntegrationTests/AuditProvenanceTests.cs`. Append both tests below **inside
the existing class**, immediately before its closing brace. They use `AuditEvent.Create` directly rather
than the file's `Event(...)` helper, because the helper mints a random `Guid` and these tests must pin
theirs.

**PROTECTED — transcribe verbatim, do not repair:**

```csharp
    // #508 — two reportable changes to one record can share an OccurredAtUtc.
    // RecordBirdMovement is how: it writes an audit event against the FLOCK's
    // id while only inserting a BirdMovement row, so it never bumps
    // Flock.Version and never serialises against Flock.Update. Two of them can
    // land in the same microsecond on one flock.
    //
    // Which of the two is reported as the last change must then be decided by a
    // DURABLE, database-assigned order — not by "Id", which AuditWriter mints as
    // a random v4 Guid and which therefore sorts arbitrarily.
    //
    // The two Guids below are PINNED, and that is the whole point of the test:
    // the event written SECOND is given the LOWER id, so a query still breaking
    // the tie on "Id" DESC names the FIRST-written actor and this test fails
    // deterministically. With a random pair it would pass about half the time,
    // which is not a regression test — it is a coin flip that occasionally
    // reports the bug.
    //
    // Each event is written in its OWN SaveChanges call so that insert order —
    // and therefore the identity column's order — is defined, rather than
    // depending on how EF batches a single AddRange.
    [Fact]
    public async Task Provenance_WhenTwoChangesShareAnInstant_NamesTheOneWrittenLast()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var sameInstant = Base.AddMinutes(10);

        var create = AuditEvent.Create(
            Guid.NewGuid(), accountId, Base, Guid.NewGuid(), "ana@farm.test",
            "Flock.Create", "Flock", entityId);

        // Written FIRST, HIGH id — this is the one the old "Id" DESC tiebreak picks.
        var first = AuditEvent.Create(
            new Guid("ffffffff-ffff-4fff-bfff-ffffffffffff"), accountId, sameInstant,
            Guid.NewGuid(), "first@farm.test", "Flock.Update", "Flock", entityId);

        // Written SECOND, LOW id — the true last change.
        var second = AuditEvent.Create(
            new Guid("00000000-0000-4000-8000-000000000001"), accountId, sameInstant,
            Guid.NewGuid(), "second@farm.test", "Flock.Update", "Flock", entityId);

        foreach (var e in new[] { create, first, second })
            await SeedEventsAsync(accountId, e);

        var result = await WithRepositoryAsync(accountId, repo =>
            repo.GetProvenanceAsync("Flock", [entityId]));

        var provenance = result[entityId];
        Assert.Equal("second@farm.test", provenance.LastChangedByEmail);
        // The instant was never at risk — only the name. Pinned so a fix that
        // reported the right person at the wrong time would still fail.
        Assert.Equal(sameInstant, provenance.LastChangedAtUtc);
    }

    // The same durability requirement, one layer out: the Audit page's own list.
    // Here a wrong order is not a wrong VALUE — both rows are shown — but the
    // pair renders in the wrong sequence, and paging needs a total order that
    // does not depend on a random Guid.
    //
    // Same pinning trick: the row written SECOND gets the LOWER id, so it must
    // come FIRST in a newest-first list, and the old "Id" DESC tiebreak puts it
    // second.
    [Fact]
    public async Task List_WhenTwoEventsShareAnInstant_ReturnsTheOneWrittenLastFirst()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var entityId = Guid.NewGuid();
        var sameInstant = Base.AddMinutes(20);

        var earlier = AuditEvent.Create(
            new Guid("ffffffff-ffff-4fff-bfff-fffffffffffe"), accountId, sameInstant,
            Guid.NewGuid(), "earlier@farm.test", "Flock.Update", "Flock", entityId);

        var later = AuditEvent.Create(
            new Guid("00000000-0000-4000-8000-000000000002"), accountId, sameInstant,
            Guid.NewGuid(), "later@farm.test", "Flock.Update", "Flock", entityId);

        foreach (var e in new[] { earlier, later })
            await SeedEventsAsync(accountId, e);

        var rows = await WithRepositoryAsync(accountId, repo =>
            repo.ListAsync(null, entityId, null, null, 10, 0));

        Assert.Collection(rows,
            row => Assert.Equal("later@farm.test", row.ActorEmail),
            row => Assert.Equal("earlier@farm.test", row.ActorEmail));
    }
```

Run **G2** filtered to these two tests, reading the command out of the G2 row rather than retyping it,
and narrowing it with `--filter "FullyQualifiedName~AuditProvenanceTests"`. Note that G2 carries
`--no-build`, so **run G1 first** — a filtered run against stale binaries is not a real red.

**Both MUST fail.** Record the failure:

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture already seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|
| G2, filtered to `FullyQualifiedName~AuditProvenanceTests` (after G1) | `Provenance_WhenTwoChangesShareAnInstant_NamesTheOneWrittenLast` | `Assert.Equal("second@farm.test", provenance.LastChangedByEmail)` | `Assert.Equal() Failure: Strings differ` with `Expected: "second@farm.test"` and `Actual: "first@farm.test"` — the literal `first@farm.test` is the discriminator and must appear | the account id and the per-test user email (`u-<guid>@test.local`) | `IAuditEventRepository.GetProvenanceAsync` → `GetProvenanceChunkAsync`'s `latest` query — the same call the five provenance-rendering endpoints reach. Not "a direct call to the method": the endpoints do exactly this, and the defect lives in the SQL, not in a caller forgetting to call it. | Nothing. `SeedAccountWithUserAsync` mints a fresh account per test and `AuditEvents` starts empty for that account, so the three events this test writes are the only rows in scope. | `Provenance_WhenTwoEventsShareAnInstant_NamesTheCreatorByAction` also asserts on a same-instant pair — but it asserts on the **creator**, chosen by ACTION, and is insensitive to the tiebreak. It stays green here, which is what separates the two. | n/a — positive test |
| G2, same narrowing | `List_WhenTwoEventsShareAnInstant_ReturnsTheOneWrittenLastFirst` | the first `Assert.Collection` element, `Assert.Equal("later@farm.test", row.ActorEmail)` | `Assert.Collection() Failure` naming `later@farm.test` as expected and `earlier@farm.test` as actual | the account id and per-test user email | `IAuditEventRepository.ListAsync`, the call `AuditEndpoints.cs:39` makes for the Audit page | nothing — fresh account, empty table for it | The other `Assert.Collection` element would also fail if the list returned one row; the two-element shape is what proves both rows were returned. | n/a — positive test |

**If either passes, or fails for a different reason, STOP and report — do not continue to 1b.**

## 1b. GREEN — map the shadow property

Edit `src/Cluckwork.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs`. Find this
exact block — it occurs exactly once in the file:

```csharp
        // Viewer: newest-first per tenant; entity drill-down.
        builder.HasIndex(e => new { e.AccountId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.AccountId, e.EntityId });
```

Replace with — **PROTECTED**:

```csharp
        // #508 — a durable monotonic ordering key. "Id" is a random v4 Guid
        // (AuditWriter), so it carries no chronology and cannot break a
        // same-instant tie: the wrong actor was being named as the last changer.
        //
        // A SHADOW property, deliberately. AuditEvent is domain data with no
        // mutation surface, and this is a persistence artifact — mapping it on
        // the type would put it in reach of anything that projects the entity.
        // Raw SQL orders by the column directly; LINQ reaches it through
        // EF.Property<long>(e, "Sequence").
        //
        // GENERATED ALWAYS, not BY DEFAULT: Postgres then REFUSES an
        // application-supplied value outright ("cannot insert a non-DEFAULT
        // value into column"), which is what keeps the ordering key
        // unforgeable by application code rather than merely unset by it.
        builder.Property<long>("Sequence")
            .ValueGeneratedOnAdd()
            .UseIdentityAlwaysColumn();

        // Viewer: newest-first per tenant; entity drill-down.
        builder.HasIndex(e => new { e.AccountId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.AccountId, e.EntityId });
```

You will need `using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;` — add it only if the build says so,
in the file's existing `using` position and alphabetical order.

## 1c. GREEN — generate the migration, then replace its body

```bash
CLUCKWORK_MIGRATIONS_CONNECTION='Host=localhost;Port=5432;Database=cluckwork;Username=x;Password=x' \
CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true \
  dotnet ef migrations add AddAuditEventSequence -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api
```

`migrations add` never connects — the placeholder credentials above are fine, and the loud insecure-
connection warning is expected. The design-time factory is fail-closed (#318), which is why the variables
must be set at all.

**Check the scaffold against the driver's probe.** The generated `Up` must be exactly one
`AddColumn<long>` with `name: "Sequence"`, `table: "AuditEvents"`, `type: "bigint"`, `nullable: false`,
`defaultValue: 0L`, and the `Npgsql:ValueGenerationStrategy` = `IdentityAlwaysColumn` annotation.
**A different operation list is a STOP.**

Now replace the generated `Up` body with the block below, leaving the class name, the file name and
`Down` as generated. **Do not "fix" the `defaultValue: 0L`** — you are deleting the whole scaffolded
operation, and that value never reached SQL anyway.

**PROTECTED — transcribe verbatim, do not repair:**

```csharp
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #508 — a durable monotonic ordering key for AuditEvents.
            //
            // This is deliberately NOT what `dotnet ef migrations add`
            // scaffolded. EF emits a single statement:
            //
            //     ALTER TABLE "AuditEvents" ADD "Sequence" bigint GENERATED ALWAYS AS IDENTITY;
            //
            // which does backfill existing rows — but in PHYSICAL order. For
            // this append-only heap that happens to be insert order today, and
            // it is not a contract: a VACUUM FULL, CLUSTER or pg_repack rewrite
            // may reorder the heap and leave "Sequence" disagreeing with
            // "OccurredAtUtc" on rows nobody ever touched. Verified by probe —
            // rows stamped 00:00:02, 00:00:01, 00:00:03 came back 1, 2, 3.
            //
            // So: add nullable, backfill in TIMESTAMP order, make NOT NULL, and
            // only then attach the identity — which is why the sequence must be
            // advanced past the backfilled values by hand.
            //
            // COALESCE(..., 0) + 1 with is_called = false is load-bearing and is
            // the EMPTY-table case, which is what every Testcontainers run hits:
            // it makes the first insert into a fresh database get 1. The obvious
            // COALESCE(max, 1) would start a fresh database at 2.
            //
            // Both arguments to pg_get_serial_sequence are quoted because the
            // table and column are both mixed-case; unquoted they fold to
            // lowercase and resolve to nothing.
            //
            // This rewrites the table under ACCESS EXCLUSIVE. So does the
            // scaffolded one-liner — the identity column assigns a per-row value
            // either way — so the sort is the only added cost, and `migrate`
            // runs as a pre-deploy job with the serving process not running DDL
            // (#263).
            // ONE STATEMENT PER Sql() CALL, deliberately. Every existing
            // migrationBuilder.Sql in this repo is a single statement — there is
            // no multi-statement precedent here, and this is not the migration to
            // establish one. EF wraps the whole Up() in a single transaction, so
            // five calls are exactly as atomic as one string would have been.
            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ADD COLUMN "Sequence" bigint;
                """);

            migrationBuilder.Sql("""
                UPDATE "AuditEvents" AS e
                SET "Sequence" = s.rn
                FROM (
                    SELECT "Id",
                           row_number() OVER (ORDER BY "OccurredAtUtc" ASC, "Id" ASC) AS rn
                    FROM "AuditEvents"
                ) AS s
                WHERE e."Id" = s."Id";
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ALTER COLUMN "Sequence" SET NOT NULL;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AuditEvents" ALTER COLUMN "Sequence" ADD GENERATED ALWAYS AS IDENTITY;
                """);

            migrationBuilder.Sql("""
                SELECT setval(
                    pg_get_serial_sequence('"AuditEvents"', 'Sequence'),
                    COALESCE((SELECT max("Sequence") FROM "AuditEvents"), 0) + 1,
                    false);
                """);
        }
```

## 1d. GREEN — use the new key in all five order clauses

Edit `src/Cluckwork.Infrastructure/Repositories/AuditEventRepository.cs`. There are **five** changes.
Each `find` block below occurs exactly once in the file; the driver counted before dispatch.

**1d-i — `ListAsync`.** Find:

```csharp
            // Id tiebreaker: same-instant events must page stably.
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
```

Replace with — **PROTECTED**:

```csharp
            // #508 — "Sequence" tiebreaker, not "Id": same-instant events must
            // page stably AND in the order they were written. "Id" is a random
            // v4 Guid, so it gave a stable-but-arbitrary order.
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => EF.Property<long>(e, "Sequence"))
```

**1d-ii — the `created` query.** Find:

```sql
              AND "Action" LIKE {createAction}
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
```

Replace with:

```sql
              AND "Action" LIKE {createAction}
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Sequence" ASC
```

**1d-iii — the `creator` CTE.** Find:

```sql
                WHERE "Action" LIKE {createAction}
                ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
```

Replace with:

```sql
                WHERE "Action" LIKE {createAction}
                ORDER BY "EntityId", "OccurredAtUtc" ASC, "Sequence" ASC
```

**1d-iv — the `latest` query.** This is the one the defect lives in. Find:

```sql
            ORDER BY e."EntityId", e."OccurredAtUtc" DESC, e."Id" DESC
```

Replace with:

```sql
            ORDER BY e."EntityId", e."OccurredAtUtc" DESC, e."Sequence" DESC
```

**1d-v — the `promoted` query.** Find:

```sql
              AND "Action" = ANY({promotionActions})
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Id" ASC
```

Replace with:

```sql
              AND "Action" = ANY({promotionActions})
            ORDER BY "EntityId", "OccurredAtUtc" ASC, "Sequence" ASC
```

## 1e. GREEN — replace the residual comment with what shipped

Still in `AuditEventRepository.cs`. Find this exact block (it occurs once) and replace it:

```csharp
        // KNOWN RESIDUAL, accepted rather than overlooked (#508). Which candidate
```

…through to the end of that comment paragraph, ending at:

```csharp
        // NO test pinning the current arbitrary outcome: that would promote a
        // known loss into a specification.
```

Replace the whole block with — **PROTECTED**:

```csharp
        // #508, FIXED. Which candidate wins is decided by ORDER BY, and the
        // tiebreak is now "Sequence" — a bigint GENERATED ALWAYS AS IDENTITY
        // column the database assigns at insert. Two reportable changes sharing
        // an OccurredAtUtc therefore resolve to the one written LAST, instead of
        // to whichever random v4 Guid happened to sort first.
        //
        // All FOUR order clauses in this method take it, not just this one. The
        // other three were once justified as "at most one row survives the
        // filter anyway" — true, but enforced only by the AGGREGATE's state
        // machine (no second Flock.Create; no path back to Draft), never by
        // anything in the audit schema. An unenforced invariant is not a reason
        // to leave a known-arbitrary tiebreak in place, and using the same
        // column costs nothing.
        //
        // Held by Provenance_WhenTwoChangesShareAnInstant_NamesTheOneWrittenLast,
        // which pins both Guids so the old behaviour fails deterministically
        // rather than half the time.
        //
        // What this does NOT fix: OccurredAtUtc is still the primary sort and
        // still comes from wall time, so a CLOCK ROLLBACK can give a later
        // insert an earlier timestamp, and "Sequence" is never consulted. Fixing
        // that means redefining "latest" as insert order for every pair, tied or
        // not — a behaviour change well outside #508.
```

Also update the comment on the `created` query, which still explains why position cannot be trusted.
Find:

```csharp
        // Creation is identified by its ACTION, never by its position in the
        // trail (codex review of PR #503). Position cannot answer it: events
        // sharing an OccurredAtUtc have no knowable order, because AuditWriter
        // mints a random v4 Guid — so an Id tiebreaker would name whichever
        // event happens to sort first, reversing creator and changer at random.
        // The action is unambiguous, and there is at most one per entity.
```

Replace with:

```csharp
        // Creation is identified by its ACTION, never by its position in the
        // trail (codex review of PR #503). That stays true after #508 gave the
        // trail a durable order: "Sequence" makes the order KNOWABLE, but the
        // earliest event is still not the same thing as the creation — a record
        // predating #494 has changes and no creation event at all. The action is
        // unambiguous; position, however well ordered, is not.
```

## 1f. Build and re-run

Run **G1** exactly as its row records it, then **G2** filtered to
`FullyQualifiedName~AuditProvenanceTests`. Do not retype either command.

Both new tests must now PASS, and the other 43 must still pass — **45 passed, 0 failed**. A drop below 43
pre-existing passes is a STOP.

## 1g. Commit Increment 1

```bash
git add src/Cluckwork.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs \
        src/Cluckwork.Infrastructure/Repositories/AuditEventRepository.cs \
        src/Cluckwork.Infrastructure/Persistence/Migrations/ \
        tests/Cluckwork.Api.IntegrationTests/AuditProvenanceTests.cs
git commit -m "fix(api): break same-instant audit ties on a database-assigned key"
```

===================================================================================
# INCREMENT 2 — generated schema docs
===================================================================================

No red phase: run the generator and commit its output.

**Shared fixture state at capture:** no shared state — `generate.sh` starts its own ephemeral Postgres,
applies the migrations with the `migrate` verb, runs `tbls`, and tears everything down. It must run
**after** increment 1, which is what creates the migration it reads.

```bash
tools/schema-docs/generate.sh
git status --short docs/schema/
git add docs/schema/
git commit -m "docs(schema): regenerate for the AuditEvents Sequence column"
```

`git status` should show **exactly three** modified files: `public.AuditEvents.md`, `README.md` and
`viewpoint-4.md`. Fewer means the generator did not pick up the migration; more means it picked up
something this slice did not change — either way, STOP and report rather than committing.

Then run **G3** to confirm the committed docs are current. **Never hand-edit `docs/schema/`** (#417) — if
the diff looks wrong, the migration is wrong.

===================================================================================
# MUTATION CHECKS — prove the guards bite
===================================================================================

Apply the mutation → the NAMED test must go RED → restore → **rebuild** → confirm green. Every mutant
below compiles. Mark each in place with `// MUTANT M<n>: <what this breaks>` and delete the marker on
restore.

**Closed-set table:** `n/a — this guard's input is not a closed set.` The tiebreak is an ordering over an
unbounded set of rows, not a decision over an enumerated set of members.

**Multi-surface table:** the acceptance criterion "a same-instant pair resolves to the one written last"
is implemented on **two** surfaces that do not share an implementation — the raw-SQL provenance query and
the LINQ list query. One row each:

| Criterion | Surface | Shared with, and how established | Mutation on that surface | Named test that must go RED | Observed | Rebuild command run |
|---|---|---|---|---|---|---|
| A same-instant pair resolves to the one written last | `GetProvenanceChunkAsync`'s `latest` raw SQL | none — implemented separately here. The two surfaces share only the `Sequence` column; the ordering is written out independently in SQL and in LINQ, with no shared helper (read: there is no ordering helper in this file). | change `e."Sequence" DESC` back to `e."Id" DESC` in the `latest` query | `Provenance_WhenTwoChangesShareAnInstant_NamesTheOneWrittenLast` | *(implementer fills)* | *(implementer fills — both builds)* |
| A same-instant pair resolves to the one written last | `ListAsync` LINQ | none — implemented separately here | change `ThenByDescending(e => EF.Property<long>(e, "Sequence"))` back to `ThenByDescending(e => e.Id)` | `List_WhenTwoEventsShareAnInstant_ReturnsTheOneWrittenLastFirst` | *(implementer fills)* | *(implementer fills — both builds)* |

**Main table:**

| # | Kind | Mutate | Supplied elsewhere? | Expected test | Expected result + failure | Rebuild command run | Observed failure |
|---|---|---|---|---|---|---|---|
| C | control | `AuditEventConfiguration.cs`: reword the comment line `// Viewer: newest-first per tenant; entity drill-down.` to `// Viewer index.` — `grep -rn "newest-first per tenant" tests/` returns nothing, so no test reads it | n/a — not a deletion | *(none)* | **GREEN** — report it green, or the way you are reading these rows cannot tell RED from GREEN | G1, then G2 filtered to `FullyQualifiedName~AuditProvenanceTests` | *(implementer fills)* |
| 1 | guard | `AuditEventRepository.cs`, `latest` query: `e."Sequence" DESC` → `e."Id" DESC` | n/a — wrong value, not a deletion | `Provenance_WhenTwoChangesShareAnInstant_NamesTheOneWrittenLast` | **RED** — `Assert.Equal("second@farm.test", provenance.LastChangedByEmail)` fails with `Actual: "first@farm.test"` | G1, then G2 filtered as above | *(implementer fills)* |
| 2 | guard | `AuditEventRepository.cs`, `ListAsync`: `EF.Property<long>(e, "Sequence")` → `e.Id` | n/a — wrong value, not a deletion | `List_WhenTwoEventsShareAnInstant_ReturnsTheOneWrittenLastFirst` | **RED** — the first `Assert.Collection` element fails, expecting `later@farm.test` | G1, then G2 filtered as above | *(implementer fills)* |
| 3 | guard | `AuditEventRepository.cs`, `latest` query: `e."OccurredAtUtc" DESC` → `e."OccurredAtUtc" ASC` | n/a — wrong value, not a deletion | `Provenance_WithSeveralEvents_ReportsTheEarliestAndTheLatest` | **RED** — proves the new tiebreak did not quietly become the *primary* sort; the timestamp ordering must still be doing the work | G1, then G2 filtered as above | *(implementer fills)* |
| 4 | guard | The migration: delete the `ALTER COLUMN "Sequence" ADD GENERATED ALWAYS AS IDENTITY;` line, leaving the column NOT NULL with no generator | **Yes, and this is the point** — with no identity, every new row would need a value from application code, which nothing supplies. Read: `AuditWriter` never sets `Sequence`, and it is a shadow property, so no code path can. | any `AuditProvenanceTests` test that writes an event | **RED** — insert fails with a NOT NULL violation on `"Sequence"`. This proves the identity clause is what makes new rows orderable at all, rather than the column merely existing. | G1, then G2 filtered as above | *(implementer fills)* |
| 5 | guard | The migration: change the backfill's `ORDER BY "OccurredAtUtc" ASC, "Id" ASC` to `ORDER BY "Id" ASC` | n/a — wrong value, not a deletion | **expected GREEN — report it as a surviving mutant, do not "fix" it** | This is a KNOWN gap and the row exists to make it visible rather than to hide it: no test seeds rows *before* the migration runs, so the backfill's ordering is unobserved by the suite. Report it green with that explanation. If it goes RED, something else is depending on backfill order and the driver wants to know. | G1, then G2 filtered as above | *(implementer fills)* |

At the end of the run:

```bash
git grep -n -e MUTANT -e 'DEBUG-' -- src tests
```

MUST return nothing. Tracked text only — do not run a recursive tree grep, which matches build output.

===================================================================================
# INCREMENT 3 — commit the planning documents
===================================================================================

This repo commits its slice documents alongside the change (see `docs/plans/562-tenant-write-token/`
and `docs/plans/670-user-roles-account-id/`). They are not on the pinned base commit, so copy them in
from the absolute paths given in your dispatch:

```bash
mkdir -p docs/plans/508-audit-monotonic-order
cp /home/mforce/dev/cluckwork/docs/plans/508-audit-monotonic-order/01-diagnosis.md \
   /home/mforce/dev/cluckwork/docs/plans/508-audit-monotonic-order/02-implementer-runbook.md \
   /home/mforce/dev/cluckwork/docs/plans/508-audit-monotonic-order/00-delivery-contract.md \
   docs/plans/508-audit-monotonic-order/
git add docs/plans/508-audit-monotonic-order/
git commit -m "docs(plans): record the #508 diagnosis, contract and runbook"
```

Copy them **verbatim**. They are the record of what was decided and why; do not edit them to match what
you did. If what you did diverges from them, that divergence goes in your report, not into these files.

**If a guard fires on the CONTENT of a copied document, that is a driver defect, not your call.** These
files become *tracked* at this commit, so guards that walk every tracked file start applying to them —
`SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` is the one that has
actually fired here, on a bare image tag written in the driver's probe narrative. Do **not** resolve it
yourself in either direction: editing the copied doc breaks "verbatim", and adding the file to the
guard's allow-list relaxes a pin guard, which is out of scope for this PR. STOP and report it; the
driver fixes the source document and you re-copy. That is exactly what happened on the first run of this
runbook, and the stop was the correct call.

===================================================================================
# FINAL VERIFICATION — run every gate unfiltered, in this order
===================================================================================

Everything above ran **G2 filtered**. This phase is the unfiltered run, and it is what the FINISH report
quotes. Run these in order, in the FOREGROUND, after the mutants are restored and the grep above is
clean:

```bash
# G4 first — G1 carries --no-restore and will fail against an unrestored tree.
dotnet restore Cluckwork.sln --locked-mode

# G1
dotnet build Cluckwork.sln --configuration Release --no-restore

# G2, unfiltered. ~3 minutes, mostly the integration project. Do NOT background it.
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal

# G3
tools/schema-docs/generate.sh --check

# The model and the migrations must agree — an acceptance criterion of this slice.
CLUCKWORK_MIGRATIONS_CONNECTION='Host=localhost;Port=5432;Database=cluckwork;Username=x;Password=x' \
CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true \
  dotnet ef migrations has-pending-model-changes -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api
```

Expected:

- **G4** — `Restored` for every project, no `NU1004`. (A `NU1004` means a package changed; nothing in this
  slice adds one, so that is a STOP.)
- **G1** — `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`. Warnings are errors here.
- **G2** — four `Passed!` lines, each `Failed: 0`, totalling **2304** (baseline 2302 + the two new tests).
- **G3** — exits 0, no diff.
- **has-pending-model-changes** — `No changes have been made to the model since the last migration.` If it
  reports pending changes, the configuration and the migration disagree: STOP and report, do **not**
  scaffold a second migration (#407 — one migration per change).

**Reporting rules, all three load-bearing:**
- If a mutation as written will NOT compile, **say so**. Do NOT substitute a different mutation and report
  the substitute's result as the real one.
- Never report a result you did not execute.
- Before reporting a mutant GREEN, prove the mutation reached the code path. Row 5 above is the exception
  only because its expected-green reasoning is written out in advance.

===================================================================================
# FINISH — push + PR
===================================================================================

Write the PR body to `/tmp/cluckwork-508-pr-body.md` — **outside the repo**, so it does not violate the
file allow-list. It must contain: what the defect was and how it was reachable; the two new tests and what
each pins; **the mutation table with your observed results**; the size/lock note from the migration
comment; the final-verification numbers; and the two deferred residuals (clock rollback; 38 sibling
`ThenBy(x => x.Id)` sites) named as out of scope, pointing at
`docs/plans/508-audit-monotonic-order/01-diagnosis.md`.

Then, verbatim:

```bash
git push -u origin fix/508-audit-monotonic-order
gh pr create --title "fix(api): order same-instant audit events by a durable monotonic key" \
             --body-file /tmp/cluckwork-508-pr-body.md
```

The PR title above is the release note — it becomes the squashed commit subject and release-please parses
it. Do not change its shape.

Open the PR **before** any review is requested, so every finding and fix SHA lands as PR history.

**Report back:** branch name, PR number, the exact G1 output tail, the final summary line of the
foreground full-suite G2 run, and the result of **every** mutation row. If any step could not be completed
as written, say which and stop — do not improvise a substitute.

Also confirm, per increment, that **you** applied its code blocks from this runbook. Name any block that
was already present when you got there, or that you did not apply yourself.
