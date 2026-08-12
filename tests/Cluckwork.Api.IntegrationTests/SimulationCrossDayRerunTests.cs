namespace Cluckwork.Api.IntegrationTests;

using System.IO;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// #279 review (codex, BLOCKER + re-check) — idempotency of the simulation seed
// under a controllable clock. The seeder writes date-relative natural keys
// (daily entries, orders, inventory, recurring drips — all `today.AddDays(-n)`),
// and both its anchor AND its "already seeded?" signal live in a durable
// SimulationSeedState row rather than being inferred from the fixture rows. This
// factory injects an advanceable IClock (no auto-seed — each test drives its own
// SeedAsync calls) so two isolated test classes can prove:
//   - a re-run after a UTC-midnight rollover converges (AlreadySeeded, unchanged
//     fingerprint, no shifted duplicate rows), reusing the durable anchor;
//   - a run whose durable row has no completion marker (a crashed prior run)
//     recovers THAT row's anchor and reports Seeded, not AlreadySeeded.
public sealed class SimulationMutableClockFactory : CluckworkWebApplicationFactory, IAsyncLifetime
{
    public const string TimeZoneId = "America/Chicago";

    // Shallow (same reasoning as SimulationSeedFactory): keep the O(HistoryDays *
    // flocks) real-handler seed loop fast while still clearing MinSentinelAgeDays.
    public const int HistoryDays = 12;

    // Runtime-generated — never a hardcoded credential (repo policy).
    public string AdminEmail { get; } = $"sim-mc-admin-{Guid.NewGuid():N}@test.local";
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    public string ManifestPath { get; } =
        Path.Combine(Path.GetTempPath(), $"sim-mc-manifest-{Guid.NewGuid():N}.json");

    // Fixed, in the past, so seeded dates never depend on the real wall clock —
    // each test sets this explicitly. Shared singleton (see the IClock override
    // below), so a scope created after the test advances it reads the new value.
    public MutableClock Clock { get; } = new(new DateOnly(2026, 6, 15));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", CastPassword);
        builder.UseSetting("Simulation:TimeZoneId", TimeZoneId);
        builder.UseSetting("Simulation:HistoryDays", HistoryDays.ToString());
        builder.UseSetting("Simulation:CredentialOutputPath", ManifestPath);

        // Replace the real SystemClock with the controllable one. Registered as
        // a singleton so the seeder (scoped) and FarmClock (scoped, resolves
        // IClock for the future-date rule) both see the SAME advanceable clock.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    // #283 — base init (start Postgres, build host, migrate — the account/
    // Admin role/egg grades ship WITH that migration now) is exactly what's
    // wanted; each test still drives its own simulation seed runs. The one
    // thing base init no longer provides is the Owner admin (no runtime
    // seeder creates it), so it's seeded here — standing in for a real
    // `bootstrap-admin` run — before any test's SeedOnceAsync() depends on it.
    // Re-declaring IAsyncLifetime is required so xUnit dispatches to THIS
    // override (the base methods aren't virtual — a `new` method alone is
    // silently skipped when called through the IAsyncLifetime reference xUnit
    // holds).
    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await this.SeedUserAsync(SeedDefaults.AccountId, AdminEmail, Roles.Owner);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
        }
        catch
        {
            // Cleanup only — never fail the suite over a stray temp file.
        }
    }

    public async Task<SeedResult> SeedOnceAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
    }

    public async Task<SimulationManifest> ReadManifestAsync()
    {
        await using var stream = File.OpenRead(ManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<SimulationManifest>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {ManifestPath}.");
    }

    // Advanceable IClock. UtcNow is anchored at NOON so converting to a
    // west-of-UTC farm zone (America/Chicago) stays the SAME calendar day —
    // keeping the tests' own tz math deterministic rather than flaking on a
    // midnight-UTC boundary.
    public sealed class MutableClock(DateOnly today) : IClock
    {
        public DateOnly Today { get; set; } = today;

        public DateTime UtcNow => Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

        public DateOnly TodayUtc => Today;

        public DateOnly TodayInZone(string timeZoneId)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, tz));
        }
    }
}

public sealed class SimulationCrossDayRerunTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task Rerun_AfterUtcDayRollover_ConvergesToAlreadySeeded_WithUnchangedFingerprintAndNoDuplicateRows()
    {
        // Day D — the genuine first run against the fresh, base-seeded database.
        factory.Clock.Today = new DateOnly(2026, 6, 15);
        var first = await factory.SeedOnceAsync();
        Assert.Equal(SeedStatus.Seeded, first.Status);

        var firstManifest = await factory.ReadManifestAsync();
        var afterFirst = await SnapshotAsync();

        // The durable state row was written: anchor recorded == the manifest's
        // anchor, and the completion marker is set once the manifest succeeded.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stateRow = await db.SimulationSeedStates.IgnoreQueryFilters()
                .SingleAsync(s => s.AccountId == SeedDefaults.AccountId);
            Assert.Equal(firstManifest.GeneratedAtAnchor, stateRow.Anchor);
            Assert.NotNull(stateRow.CompletedAtUtc);
        }

        // Advance the wall clock past UTC midnight — the exact scenario codex
        // flagged. A naive fresh-clock anchor would re-derive every dated key one
        // day later and write a second, shifted copy.
        factory.Clock.Today = new DateOnly(2026, 6, 16);
        var second = await factory.SeedOnceAsync();

        // Converges: AlreadySeeded (not Seeded, not Failed) and exit-0 semantics.
        Assert.Equal(SeedStatus.AlreadySeeded, second.Status);
        Assert.True(second.IsSuccess, second.Message);

        var secondManifest = await factory.ReadManifestAsync();
        var afterSecond = await SnapshotAsync();

        // The anchor was reused from the durable row, not re-read from the
        // advanced clock — so the manifest's anchor, fingerprint, and counts are
        // all identical to day D's.
        Assert.True(secondManifest.Complete);
        Assert.Equal(firstManifest.GeneratedAtAnchor, secondManifest.GeneratedAtAnchor);
        Assert.Equal(firstManifest.Fingerprint, secondManifest.Fingerprint);
        Assert.Equal(firstManifest.Counts, secondManifest.Counts);

        // And no shifted duplicates actually landed in the database.
        Assert.Equal(afterFirst, afterSecond);
    }

    // Row counts for every date-relative fixture the anchor drives — a shifted
    // re-run would inflate at least the daily-entry, egg-lot, and order counts.
    private async Task<(int Accounts, int DailyEntries, int EggLots, int SalesOrders)> SnapshotAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accounts = await db.Accounts.IgnoreQueryFilters().CountAsync();
        var dailyEntries = await db.DailyEntries.IgnoreQueryFilters()
            .CountAsync(e => e.AccountId == SeedDefaults.AccountId);
        var eggLots = await db.EggLots.IgnoreQueryFilters()
            .CountAsync(l => l.AccountId == SeedDefaults.AccountId);
        var salesOrders = await db.SalesOrders.IgnoreQueryFilters()
            .CountAsync(o => o.AccountId == SeedDefaults.AccountId);
        return (accounts, dailyEntries, eggLots, salesOrders);
    }
}

// #279 review (codex re-check, findings #1 + #2) — the durable-anchor / durable
// completion-marker guarantees, isolated on their own database (own factory
// instance via IClassFixture).
public sealed class SimulationDurableSeedStateTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task PriorRunWithoutCompletionMarker_RecoversItsDurableAnchor_AndReportsSeeded()
    {
        // Simulate a first run that crashed AFTER persisting its anchor (the very
        // first thing SeedAsync writes) but BEFORE the completion marker — no
        // manifest was ever emitted. The anchor is deliberately in the PAST and
        // 10 days off the clock's "today", so the assertions prove the seeder
        // takes the anchor from the durable row, not the wall clock (and thus is
        // immune to any foreign daily entry a load test wrote into the account —
        // a max(entry-date) recovery would not be).
        var crashedAnchor = new DateOnly(2026, 6, 10);
        factory.Clock.Today = new DateOnly(2026, 6, 20);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SimulationSeedStates.Add(new SimulationSeedState
            {
                AccountId = SeedDefaults.AccountId,
                Anchor = crashedAnchor,
                CompletedAtUtc = null, // never completed
            });
            await db.SaveChangesAsync();
        }

        var result = await factory.SeedOnceAsync();

        // A prior run that never set the completion marker is NOT AlreadySeeded —
        // this run finishes the fixture and reports Seeded.
        Assert.Equal(SeedStatus.Seeded, result.Status);

        // Seeded against the DURABLE anchor (2026-06-10), not the clock's "today"
        // (2026-06-20).
        var manifest = await factory.ReadManifestAsync();
        Assert.Equal(crashedAnchor, manifest.GeneratedAtAnchor);

        // The marker is now set, so a plain re-run (nothing changed) is a no-op:
        // AlreadySeeded.
        var again = await factory.SeedOnceAsync();
        Assert.Equal(SeedStatus.AlreadySeeded, again.Status);
    }
}

// #279 review (codex re-check, finding 2) — a definition change after completion
// must re-run as Seeded, not a silent AlreadySeeded. Own database (own factory
// instance) because it seeds a full fixture and must not share state with the
// durable-anchor test above.
public sealed class SimulationSeedDefinitionChangeTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task DefinitionChangeAfterCompletion_ReportsSeeded_NotSilentAlreadySeeded()
    {
        // A completed fixture: first run Seeded, plain re-run AlreadySeeded.
        factory.Clock.Today = new DateOnly(2026, 6, 15);
        Assert.Equal(SeedStatus.Seeded, (await factory.SeedOnceAsync()).Status);
        Assert.Equal(SeedStatus.AlreadySeeded, (await factory.SeedOnceAsync()).Status);

        // Now the fixture no longer matches its definition — modelled by deleting
        // the pristine second account (the same count-changing signal a
        // SimulationOptions change would produce). The durable completion marker
        // is still set, but this run RE-CREATES the missing row, so it must
        // report Seeded, not a misleading AlreadySeeded.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Accounts.IgnoreQueryFilters()
                .Where(a => a.Id == SimulationDataSeeder.SecondAccountId)
                .ExecuteDeleteAsync();
        }

        var afterChange = await factory.SeedOnceAsync();
        Assert.Equal(SeedStatus.Seeded, afterChange.Status);

        // And it settles back to AlreadySeeded once nothing more changes.
        Assert.Equal(SeedStatus.AlreadySeeded, (await factory.SeedOnceAsync()).Status);
    }
}

// #500 — the only test that can reach the failing state of
// RestrictOneWorkerAsync's IDEMPOTENT branch.
//
// Its own factory instance (xUnit gives every test class its own IClassFixture
// instance, hence its own Postgres container), which is required rather than
// tidy: this test DELETES a seeded row and re-seeds, and a mutating test
// sharing a fixture with assertions about that fixture's contents is
// order-dependent by construction.
//
// Why no existing test reaches it: SimulationSeedFactory seeds once, and the
// cross-day tests above deliberately reuse the durable anchor so the day range
// is identical — every entry short-circuits on its natural key before WorkerFor
// is ever evaluated. So a branch that reported (Guid.Empty, Guid.Empty) instead
// of the existing pair would stay green forever, and fail only in a real
// partial re-run, where it takes the WHOLE seed down via FlockScopeGuard.
public sealed class SimulationPartialRerunTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_PartialRerunReconstructsWithAnEligibleWorker()
    {
        var first = await factory.SeedOnceAsync();
        Assert.True(first.IsSuccess, $"first seed failed: {first.Message}");

        var (restrictedWorkerId, otherFlockId, deletedDate) = await factory.WithTenantScopeAsync(
            SeedDefaults.AccountId, async db =>
            {
                var assignment = await db.UserRoleAssignments.IgnoreQueryFilters()
                    .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
                    .SingleAsync();

                // A DRAFT entry on the flock the restricted worker is NOT
                // assigned to. Draft is load-bearing: a submitted entry has
                // already minted egg lots, and deleting it would orphan them and
                // fail the re-run's own exact-count validation for reasons that
                // have nothing to do with what this test is about.
                var victim = await db.DailyEntries.IgnoreQueryFilters()
                    .Where(e => e.AccountId == SeedDefaults.AccountId
                                && e.FlockId != assignment.FlockId!.Value
                                && e.Status == DailyEntryStatus.Draft)
                    .OrderBy(e => e.Date)
                    .FirstAsync();

                var pair = (assignment.UserId, victim.FlockId, victim.Date);

                // Feed and water usage carry a nullable DailyEntryId under a
                // RESTRICT foreign key, and the seeder's usage window (4 days)
                // covers the whole draft window (2 days) — so EVERY draft entry
                // is referenced and the delete below fails without this.
                //
                // The feed usage's own InventoryMovement goes too. Removing the
                // usage row alone leaves the movement behind, the re-run writes
                // a second one, and the seed then fails its exact-count check
                // ("inventoryMovements.usage: expected 8, got 9") for a reason
                // that has nothing to do with what this test is about. All three
                // rows are recreated by the re-run — each has its own
                // (flock, item, date) / (flock, date) existence check — so the
                // fixture converges on the same counts.
                await db.InventoryMovements.IgnoreQueryFilters()
                    .Where(m => m.Type == InventoryMovementType.Usage
                                && m.FlockId == victim.FlockId && m.Date == victim.Date)
                    .ExecuteDeleteAsync();
                await db.FeedUsages.IgnoreQueryFilters()
                    .Where(u => u.DailyEntryId == victim.Id).ExecuteDeleteAsync();
                await db.WaterUsages.IgnoreQueryFilters()
                    .Where(u => u.DailyEntryId == victim.Id).ExecuteDeleteAsync();

                db.DailyEntries.Remove(victim); // grades cascade (DailyEntryConfiguration)
                await db.SaveChangesAsync();
                return pair;
            });

        // The partial re-run. The day range is unchanged, so every OTHER entry
        // short-circuits on its natural key and only the deleted one is
        // rebuilt — the single path that evaluates WorkerFor on a re-run, and
        // therefore the only one that can observe a RestrictedFlockId the
        // idempotent branch failed to report.
        var second = await factory.SeedOnceAsync();
        Assert.True(second.IsSuccess, $"partial re-run failed: {second.Message}");

        await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
        {
            var replacement = await db.DailyEntries.IgnoreQueryFilters()
                .SingleAsync(e => e.AccountId == SeedDefaults.AccountId
                                  && e.FlockId == otherFlockId && e.Date == deletedDate);

            var create = await db.AuditEvents.IgnoreQueryFilters()
                .Where(e => e.EntityId == replacement.Id && e.Action == AuditActions.DailyEntryCreate)
                .SingleAsync();

            // The authorization clause: the rebuilt entry is on the foreign
            // flock, so its author must not be the restricted worker.
            Assert.NotEqual(restrictedWorkerId, create.ActorUserId);

            // Ordered by email to reproduce the seeder's creation order — which
            // it does only while the pool is single-digit (`sim-worker-10` sorts
            // before `sim-worker-2`). Safe at the configured 3.
            var workers = await db.Users
                .Where(u => u.AccountId == SeedDefaults.AccountId && u.Email!.StartsWith("sim-worker-"))
                .OrderBy(u => u.Email)
                .Select(u => new { u.Id, u.Email })
                .ToListAsync();

            // It must be an actual WORKER, not Pick's manager/Owner fallback —
            // which is what a WorkerFor whose pool filtered down to nothing
            // would produce, satisfying the clause above while degrading.
            Assert.Contains(create.ActorUserId, workers.Select(w => w.Id));

            // ...and it must be the SPECIFIC eligible worker the rotation
            // selects. This clause is why the test exists in this form, and it
            // was added only after watching the mutation survive without it:
            // at the draft window's day offsets, a RestrictOneWorkerAsync that
            // reported (Empty, Empty) still picks a NON-restricted worker —
            // just a different one — because the unfiltered pool has 3 members
            // and the filtered pool has 2. "Not the restricted worker" and "is
            // a worker" are both true either way, so neither can see the bug.
            //
            // Re-deriving the rule here is deliberate. It is the determinism
            // contract (#279) — a fixture whose attribution varies run to run
            // is exactly what this seeder may not do — and re-deriving it is
            // the only thing that makes the idempotent branch observable.
            var anchor = (await db.SimulationSeedStates.IgnoreQueryFilters()
                .SingleAsync(s => s.AccountId == SeedDefaults.AccountId)).Anchor;
            var dayOffset = anchor.DayNumber - deletedDate.DayNumber;
            var eligible = workers.Where(w => w.Id != restrictedWorkerId).ToList();
            var expected = eligible[dayOffset % eligible.Count];

            Assert.Equal(expected.Id, create.ActorUserId);
        });
    }
}

// #500 final review — a cast persona whose role was changed outside the seeder
// must fail LOUDLY and name the cause, not be silently accepted into the wrong
// pool.
//
// Why this matters more than "the fixture is a bit wrong": a manager demoted to
// ReadOnly still holds no UserRoleAssignment rows, so FlockScopeGuard would let
// it through on the "zero rows = account-wide" branch rather than refusing it.
// The seed would keep going, write durable rows, and only fail much later on an
// exact-count mismatch whose message points at counts rather than at the cause.
//
// Own factory instance (own container): this test mutates a seeded user.
public sealed class SimulationReconfiguredCastTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WhenACastPersonaLostItsRole_FailsNamingTheRole()
    {
        var first = await factory.SeedOnceAsync();
        Assert.True(first.IsSuccess, $"first seed failed: {first.Message}");

        // Demote the manager through UserManager, exactly as an operator poking
        // at the fixture would.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = await users.FindByEmailAsync("sim-manager-1@sim.local");
            Assert.NotNull(manager);
            Assert.True((await users.RemoveFromRoleAsync(manager!, Roles.Manager)).Succeeded);
            Assert.True((await users.AddToRoleAsync(manager!, Roles.ReadOnly)).Succeeded);
        }

        var second = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.Failed, second.Status);
        // The message must name the persona AND the role it no longer holds —
        // "the seed failed" alone would be satisfied by any unrelated fault.
        Assert.Contains("sim-manager-1@sim.local", second.Message);
        Assert.Contains(Roles.Manager, second.Message);
    }
}
