namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #638 — a fixture deep enough that the daily-entry lock sweep CANNOT drain in
// one pass.
//
// DailyEntryLockSweep locks at most DailyEntryLockSweep.BatchSize entries per
// pass per account; SimulationDataSeeder used to call it once, while
// ExpectedLockedEntryCount expects every entry past the lock cutoff to be
// Locked. Those agree only while
// `FlockTopologyCount(2) * (HistoryDays - LockAfterDays(7)) <= BatchSize(200)`
// — i.e. HistoryDays <= 107. This fixture sits above that line, so before the
// drain landed the seed could not validate here on any database, however
// clean.
//
// Own factory (own Postgres container), same reasoning as SimulationSeedFactory:
// it writes to the fixed SeedDefaults.AccountId, so it cannot share a container
// with another seeder.
public sealed class DeepSimulationSeedFactory : CluckworkWebApplicationFactory, IAsyncLifetime
{
    public const string TimeZoneId = "America/Chicago";

    // The cheapest depth that still proves the point, with margin. The
    // one-pass ceiling is 107, and the exact eligible count shifts by one day's
    // worth of entries depending on how the account's farm-local "today" skews
    // against the UTC seed anchor at the moment the suite runs — so 108 (the
    // true minimum) could land on exactly 200 and pass vacuously. 112 yields
    // 208-210 eligible entries either way: always more than one pass, never
    // more than two, and the seed loop stays O(HistoryDays * flocks) round
    // trips so every extra day is real time on Testcontainers Postgres.
    public const int HistoryDays = 112;

    // Runtime-generated — never a hardcoded credential (repo policy).
    public string AdminEmail { get; } = $"deep-sim-admin-{Guid.NewGuid():N}@test.local";
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    // Captured, NOT thrown on. A failed deep seed is precisely what this class
    // is here to report, and the seeder's own count mismatch is the message
    // that says why — losing it to a fixture-initialisation exception would
    // turn a diagnosis into "the fixture blew up".
    public SeedResult SeedResult { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", CastPassword);
        builder.UseSetting("Simulation:TimeZoneId", TimeZoneId);
        builder.UseSetting("Simulation:HistoryDays", HistoryDays.ToString());
    }

    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await this.SeedUserAsync(SeedDefaults.AccountId, AdminEmail, Roles.Owner);

        using var scope = Services.CreateScope();
        SeedResult = await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
    }
}

public sealed class SimulationDeepSeedDrainTests(DeepSimulationSeedFactory factory)
    : IClassFixture<DeepSimulationSeedFactory>
{
    // The guarantee: a fixture past the one-pass ceiling validates.
    //
    // Before the drain this failed with the seeder's own completion check —
    // `dailyEntries.locked: expected N, got 200` plus the mirrored
    // `dailyEntries.submitted` overshoot, and nothing else mismatched. The
    // message is asserted along with the flag so a red run says which.
    [Fact]
    public void SimulationSeed_AboveOnePassLockSweepCeiling_Validates()
    {
        Assert.True(
            factory.SeedResult.IsSuccess,
            $"Deep simulation seed failed ({factory.SeedResult.Status}): {factory.SeedResult.Message}");
    }

    // The reason it validates, stated separately so this class cannot go green
    // for the wrong one: the sweep really did run more than once. A single pass
    // can leave at most BatchSize entries Locked, so a strictly greater count
    // is only reachable by draining. Reading it back off the database rather
    // than counting passes keeps the assertion on the outcome, not on the
    // implementation that produced it.
    [Fact]
    public async Task SimulationSeed_LocksEveryDueEntry_NotJustTheFirstBatch()
    {
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(SeedDefaults.AccountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var entries = await db.DailyEntries
            .AsNoTracking()
            .Select(e => new { e.Date, e.Status })
            .ToListAsync();

        var locked = entries.Count(e => e.Status == DailyEntryStatus.Locked);
        Assert.True(
            locked > DailyEntryLockSweep.BatchSize,
            $"Expected more than one sweep pass worth of locked entries "
            + $"(> {DailyEntryLockSweep.BatchSize}) at HistoryDays="
            + $"{DeepSimulationSeedFactory.HistoryDays}, got {locked}. Either the "
            + "drain regressed, or the fixture depth no longer clears the "
            + "one-pass ceiling and this class is now vacuous.");

        // Nothing eligible left behind: same cutoff DailyEntryLockSweep applies.
        var cutoff = clock.TodayInZone(DeepSimulationSeedFactory.TimeZoneId)
            .AddDays(-DailyEntryLockSweep.LockAfterDays);
        var stillDue = entries.Count(e => e.Status == DailyEntryStatus.Submitted && e.Date < cutoff);
        Assert.Equal(0, stillDue);
    }
}
