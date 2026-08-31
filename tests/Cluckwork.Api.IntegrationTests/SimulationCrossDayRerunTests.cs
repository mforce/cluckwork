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
    // re-run would inflate at least the daily-entry, egg-lot, and order
    // counts. #627 extended the snapshot with the over-cap bands (customers,
    // flocks, bird movements, inventory movements): a re-run that re-derived
    // the earlier placement/opening anchors would duplicate the date-keyed
    // rows in exactly these bands.
    private async Task<(int Accounts, int DailyEntries, int EggLots, int SalesOrders, int Customers, int Flocks, int BirdMovements, int InventoryMovements)> SnapshotAsync()
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
        var customers = await db.Customers.IgnoreQueryFilters()
            .CountAsync(c => c.AccountId == SeedDefaults.AccountId);
        var flocks = await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
        var birdMovements = await db.BirdMovements.IgnoreQueryFilters()
            .CountAsync(m => m.AccountId == SeedDefaults.AccountId);
        var inventoryMovements = await db.InventoryMovements.IgnoreQueryFilters()
            .CountAsync(m => m.AccountId == SeedDefaults.AccountId);
        return (accounts, dailyEntries, eggLots, salesOrders, customers, flocks, birdMovements, inventoryMovements);
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

// #500 (codex review of PR #517, then a local review of that fix) — the
// simulation seeder's disabled-Owner filter, guarded on its own.
//
// The fix landed in BOTH seeders but only DemoDataSeeder got tests. That is
// exactly the shape that ships silently: a later refactor toward shared code
// that misses one branch leaves this seeder signing its fixture with an account
// that cannot log in, and the demo tests stay green throughout.
public sealed class SimulationDisabledOwnerTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WithTheOnlyOwnerDisabled_FailsClosedNamingAFollowableRepair()
    {
        // The factory provisions its Owner in InitializeAsync; disable it, so
        // the account holds an Owner ROLE ROW and no usable Owner.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync(factory.AdminEmail);
            Assert.NotNull(owner);
            owner!.DisabledAt = DateTime.UtcNow;
            Assert.True((await users.UpdateAsync(owner)).Succeeded);
        }

        var result = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);
        Assert.Contains("DISABLED", result.Message);

        // And the remedy must be one that can actually be followed from here.
        // Neither `bootstrap-admin` (no-ops on the retained Owner role row) nor
        // the Users screen (OwnerOnly, and the only Owner is the disabled one)
        // can be reached, so the message has to name direct database repair.
        Assert.Contains("no in-product repair", result.Message);
        Assert.Contains("directly in the database", result.Message);

        // Fails closed: nothing seeded before refusing.
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Flocks.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == SeedDefaults.AccountId));
    }
}

// #500 (codex round 3) — the disabled-Owner advice must be gated on there being
// no ENABLED Owner, not merely on a disabled one existing.
//
// The simulation preflight fails for ANY missing base datum, and the egg grades
// are user-renamable (#283) — so the realistic cause is a renamed grade. If the
// account also happens to hold one disabled co-Owner beside a perfectly good
// active one, branching on `disabledOwners > 0` alone sends the operator off to
// edit DisabledAt columns while the actual fault is a grade name.
public sealed class SimulationDisabledCoOwnerTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WithAnEnabledOwnerAndADisabledCoOwner_ReportsTheRealPrerequisite()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner))
                await roles.CreateAsync(new ApplicationRole { Name = Roles.Owner });

            // A DISABLED co-Owner beside the factory's enabled one.
            var disabled = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "disabled-co-owner@test.local",
                Email = "disabled-co-owner@test.local",
                AccountId = SeedDefaults.AccountId,
                DisabledAt = DateTime.UtcNow,
            };
            Assert.True((await users.CreateAsync(disabled, TestHarness.Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(disabled, Roles.Owner)).Succeeded);

            // And the ACTUAL fault: a renamed grade, which the seeder consumes
            // by name. Renaming is a supported user action, not corruption.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var renamed = $"Renamed-{Guid.NewGuid():N}"; // unique: the name has a lower(Name) unique index
            var affected = await db.EggGrades.IgnoreQueryFilters()
                .Where(g => g.AccountId == SeedDefaults.AccountId && g.Name == "Large")
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, renamed));
            Assert.Equal(1, affected); // the fault this test depends on actually exists
        }

        var result = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);

        // It must name the base data, NOT send the operator editing DisabledAt:
        // an enabled Owner exists, so the disabled-only advice is simply false.
        Assert.Contains("base data", result.Message);
        Assert.DoesNotContain("DisabledAt", result.Message);
        Assert.DoesNotContain("no in-product repair", result.Message);

        // #500 (codex round 4) — and it must not send them to `bootstrap-admin`
        // either, which was the round-3 fix's own remaining defect. Round 3
        // stopped the message being WRONG about the cause; it still named a
        // remedy that cannot work, because FirstRunAdminService returns
        // AlreadyProvisioned whenever any Owner exists — and one does here, by
        // construction. The operator would run it, see "already provisioned",
        // re-run the seed, and land on this identical message forever.
        //
        // Asserted on the INVOCATION form, not the bare word: the message is
        // allowed to mention `bootstrap-admin` in order to rule it out, and
        // does. What must never appear is the command line to run.
        Assert.DoesNotContain("dotnet Cluckwork.Api.dll bootstrap-admin", result.Message);

        // The positive half, which is what makes the two assertions above more
        // than "says nothing useful": it has to name the thing actually broken.
        Assert.Contains("grade", result.Message);
    }
}

// #500 (codex round 4, local review) — when the base data AND the Owner are both
// missing, ONE run must report both.
//
// This is a defect the round-4 split introduced rather than one it inherited:
// two separate `if` blocks short-circuit, so the base-data block returned before
// the Owner check ran and the operator repaired the grades, re-ran, and only
// then met the second problem. Two trips for one broken database.
//
// It also stops the neighbouring test's `DoesNotContain("dotnet Cluckwork.Api.dll
// bootstrap-admin")` from being vacuous: this is the same message under the
// opposite condition, and here that exact string MUST appear.
public sealed class SimulationBaseDataAndOwnerBothMissingTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WithNeitherBaseDataNorAnOwner_ReportsBothInOneRun()
    {
        using (var scope = factory.Services.CreateScope())
        {
            // Strip the Owner ROLE rather than deleting the user: that is the
            // `disabledOwners == 0` shape, which is the branch that names
            // bootstrap-admin. (The disabled shape is covered next door.)
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await users.FindByEmailAsync(factory.AdminEmail);
            Assert.NotNull(admin);
            Assert.True((await users.RemoveFromRoleAsync(admin!, Roles.Owner)).Succeeded);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var renamed = $"Renamed-{Guid.NewGuid():N}"; // unique: lower(Name) is uniquely indexed
            var affected = await db.EggGrades.IgnoreQueryFilters()
                .Where(g => g.AccountId == SeedDefaults.AccountId && g.Name == "Large")
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, renamed));
            Assert.Equal(1, affected); // both faults this test needs really exist
        }

        var result = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);

        // Both causes, both remedies, one run.
        Assert.Contains("grade", result.Message);
        Assert.Contains("no user in the Owner role", result.Message);
        Assert.Contains("dotnet Cluckwork.Api.dll bootstrap-admin", result.Message);
    }
}

// #500 (codex round 5) — a missing Owner ROLE is a third state hiding behind
// `Owner is null`, and it must not collect `bootstrap-admin` advice.
//
// The review that prompted this claimed `GetUsersInRoleAsync` THROWS for an
// absent role, so the Owner lookup running before the base-data check would
// surface a generic `Failed` instead of the prerequisite advice. That is not
// what happens, and this test was written first precisely to find out: the
// status, "base data" and "migrate" assertions below all passed against the
// unfixed code. `UserStore.GetUsersInRoleAsync` returns an EMPTY LIST when the
// role is missing (throwing is `AddToRoleAsync`/`IsInRoleAsync`), so the
// base-data check caught it exactly as intended.
//
// The real defect was smaller, and mine — the round-4 dual-failure appendix.
// An empty list makes `owner is null` true, so a dropped-roles schema was
// indistinguishable from "nobody is an Owner yet" and the message appended
// "run bootstrap-admin", which cannot create a role. Restoring the base data,
// which the message already says, is the whole remedy in that case.
public sealed class SimulationMissingOwnerRoleTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    [Fact]
    public async Task SimulationSeed_WithNoOwnerRoleAtAll_ReportsTheBaseDataPrerequisite()
    {
        using (var scope = factory.Services.CreateScope())
        {
            // Identity cascades AspNetUserRoles, so the factory's admin loses the
            // role row with it — which is the point: this is what a restore that
            // dropped the migration-baked roles looks like.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var affected = await db.Roles.Where(r => r.Name == Roles.Owner).ExecuteDeleteAsync();
            Assert.Equal(1, affected); // the fault this test depends on actually exists
        }

        var result = await factory.SeedOnceAsync();

        // These three held before the fix too — kept because they are what makes
        // the assertion below meaningful rather than a bare negative, and because
        // they pin the behaviour the review predicted would break.
        Assert.Equal(SeedStatus.PrerequisitesMissing, result.Status);
        Assert.Contains("base data", result.Message);
        Assert.Contains("migrate", result.Message);

        // THIS is the finding. The dual-failure appendix must not fire: with no
        // Owner role at all, `bootstrap-admin` is not the next step — restoring
        // the roles is, and only the base-data message can say so.
        Assert.DoesNotContain("bootstrap-admin --email", result.Message);
    }
}

// #500 (codex round 4) — the cast is held to the same standard as the Owner.
//
// FindOwnerAsync has excluded disabled Owners since round 1. The CAST went on
// accepting them, and the two classes below are the same defect in the two
// remaining shapes. Both are re-run-only: a first run creates the personas
// itself, so nothing can be wrong with them yet. Both mutate the fixture
// destructively, so each takes its own factory instance — hence its own
// Postgres container — rather than sharing one with a neighbour.
public sealed class SimulationDisabledCastMemberTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    // Disabling keeps every role row and only stamps DisabledAt, so
    // FindByEmailAsync still returns the persona and the role check still
    // passes. Without the DisabledAt guard the re-run happily attributes newly
    // written fixture history to somebody LoginAsync rejects — and REPORTS
    // SUCCESS. That is not a guess: removing the guard turns the status
    // assertion below red on `Seeded`, because disabling moves nobody between
    // role buckets and so the manifest's exact-count validation cannot see it.
    // The rows are well-formed, the counts validate, and nothing surfaces until
    // a human tries to sign in as the persona a History line names.
    [Fact]
    public async Task SimulationSeed_WhenASeededCastMemberIsDisabled_RefusesTheRerun()
    {
        var first = await factory.SeedOnceAsync();
        Assert.True(first.IsSuccess, $"first seed failed: {first.Message}");

        var email = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.Users.IgnoreQueryFilters()
                .Where(u => u.AccountId == SeedDefaults.AccountId && u.Email!.StartsWith("sim-manager-"))
                .Select(u => u.Email!)
                .OrderBy(e => e)
                .FirstAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var persona = await users.FindByEmailAsync(email);
            Assert.NotNull(persona);
            persona!.DisabledAt = DateTime.UtcNow;
            Assert.True((await users.UpdateAsync(persona)).Succeeded);
        }

        var rerun = await factory.SeedOnceAsync();

        // Refused, not "succeeded with a disabled author".
        Assert.Equal(SeedStatus.Failed, rerun.Status);
        Assert.Contains(email, rerun.Message); // names WHICH persona, not just that one is wrong
        Assert.Contains("DISABLED", rerun.Message);
    }
}

public sealed class SimulationPromotedWorkerTests(SimulationMutableClockFactory factory)
    : IClassFixture<SimulationMutableClockFactory>
{
    // The worker persona holds NO assignable role by construction ("Worker" is a
    // pseudo-role the seeder's storedRole conversion maps to null). The role check used to skip
    // workers entirely rather than assert that emptiness, so a worker promoted
    // through the Users UI passed straight through.
    //
    // The RESTRICTED worker is promoted here on purpose — it is the one persona
    // whose narrowing the fixture exists to exercise. Manager bypasses
    // FlockScopeGuard BY ROLE, so the re-run would otherwise exercise the exact
    // opposite of the authorization shape it claims to cover.
    //
    // WHAT THE MUTATION ACTUALLY SHOWED, because it is not what the review that
    // prompted this claimed. Removing the guard does NOT make the re-run report
    // success: ValidateCounts counts users by role, so a promotion moves one
    // user between two buckets and the seed dies with
    //
    //     users.managers: expected 1, got 2; users.workers: expected 3, got 2
    //
    // That happens in EmitManifestAsync — LAST, after every durable write. So
    // the defect this guard fixes is a misleading failure late, not a silent
    // success, and the assertions below are written to pin that distinction
    // rather than the weaker "it failed somehow" (which the count check already
    // satisfies on its own). Contrast the disabled-persona case next door, which
    // IS silently green: disabling moves nobody between role buckets, so the
    // count check cannot see it at all.
    [Fact]
    public async Task SimulationSeed_WhenTheRestrictedWorkerIsPromoted_RefusesTheRerun()
    {
        var first = await factory.SeedOnceAsync();
        Assert.True(first.IsSuccess, $"first seed failed: {first.Message}");

        var email = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
        {
            // The flock-scoped assignment IS the restriction (#500): a row with a
            // non-null FlockId is what narrows this worker to one flock.
            var restricted = await db.UserRoleAssignments.IgnoreQueryFilters()
                .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
                .Select(a => a.UserId)
                .SingleAsync();

            return await db.Users.IgnoreQueryFilters()
                .Where(u => u.Id == restricted)
                .Select(u => u.Email!)
                .SingleAsync();
        });
        Assert.StartsWith("sim-worker-", email); // the restricted persona really is a worker

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var persona = await users.FindByEmailAsync(email);
            Assert.NotNull(persona);
            Assert.Empty(await users.GetRolesAsync(persona!)); // held none before the promotion
            Assert.True((await users.AddToRoleAsync(persona!, Roles.Manager)).Succeeded);
        }

        var rerun = await factory.SeedOnceAsync();

        Assert.Equal(SeedStatus.Failed, rerun.Status);

        // These three are the load-bearing ones. Status alone is satisfied by
        // the late count check (see the note above), so it proves nothing here.
        Assert.Contains(email, rerun.Message); // names WHICH persona
        Assert.Contains(Roles.Manager, rerun.Message); // and the role it actually found
        Assert.Contains("FlockScopeGuard", rerun.Message); // and why that matters

        // The guard must be what refused, not ValidateCounts several hundred
        // durable writes later. "expected" is the count check's own wording, and
        // its absence is the only thing separating "refused at the cast" from
        // "refused at the manifest" once both produce Failed.
        Assert.DoesNotContain("expected", rerun.Message);
    }
}
