namespace Cluckwork.Api.IntegrationTests;

using System.IO;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// #279 review Fix 1 (codex, BLOCKER) — the regression test for cross-UTC-day
// idempotency. The seeder writes date-relative natural keys (daily entries,
// orders, inventory, recurring drips — all `today.AddDays(-n)`). If the anchor
// were re-read from the wall clock on every run, a re-run after a UTC-midnight
// rollover would write a fresh, date-shifted copy of every dated fixture beside
// the old rows, and the exact-count manifest validation would then return
// Failed rather than AlreadySeeded. This fixture injects a CONTROLLABLE clock so
// the test can advance "today" by a calendar day between two seed runs and prove
// the run converges (AlreadySeeded, unchanged fingerprint, no duplicate rows).
public sealed class SimulationCrossDayFactory : CluckworkWebApplicationFactory, IAsyncLifetime
{
    public const string TimeZoneId = "America/Chicago";

    // Shallow (same reasoning as SimulationSeedFactory): keep the O(HistoryDays *
    // flocks) real-handler seed loop fast while still clearing MinSentinelAgeDays.
    public const int HistoryDays = 12;

    // Runtime-generated — never a hardcoded credential (repo policy).
    public string AdminEmail { get; } = $"sim-xday-admin-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    public string ManifestPath { get; } =
        Path.Combine(Path.GetTempPath(), $"sim-xday-manifest-{Guid.NewGuid():N}.json");

    // Fixed, in the past, so seeded dates never depend on the real wall clock —
    // the test drives this between the two seed runs. Shared singleton (see the
    // IClock override below), so a scope created after the test advances it reads
    // the new value.
    public MutableClock Clock { get; } = new(new DateOnly(2026, 6, 15));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
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

    // NOTE: this factory doesn't override InitializeAsync (base init — start
    // Postgres, build host, run the base boot seed — is exactly what's wanted;
    // the test drives the two simulation seed runs itself). But re-declaring
    // IAsyncLifetime is still required so xUnit dispatches DisposeAsync to THIS
    // override (the base methods aren't virtual — a `new` method alone is
    // silently skipped when called through the IAsyncLifetime reference xUnit
    // holds), otherwise the temp manifest cleanup below never runs.
    public new async Task InitializeAsync() => await base.InitializeAsync();

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

    // Advanceable IClock. UtcNow is anchored at NOON so converting to a
    // west-of-UTC farm zone (America/Chicago) stays the SAME calendar day —
    // keeping the test's own tz math deterministic rather than flaking on a
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

public sealed class SimulationCrossDayRerunTests(SimulationCrossDayFactory factory)
    : IClassFixture<SimulationCrossDayFactory>
{
    [Fact]
    public async Task Rerun_AfterUtcDayRollover_ConvergesToAlreadySeeded_WithUnchangedFingerprintAndNoDuplicateRows()
    {
        // Day D — the genuine first run against the fresh, base-seeded database.
        factory.Clock.Today = new DateOnly(2026, 6, 15);
        var first = await SeedOnceAsync();
        Assert.Equal(SeedStatus.Seeded, first.Status);

        var firstManifest = await ReadManifestAsync(factory.ManifestPath);
        var afterFirst = await SnapshotAsync();

        // Advance the wall clock past UTC midnight — the exact scenario codex
        // flagged. A naive fresh-clock anchor would re-derive every dated key one
        // day later and write a second, shifted copy.
        factory.Clock.Today = new DateOnly(2026, 6, 16);
        var second = await SeedOnceAsync();

        // Converges: AlreadySeeded (not Seeded, not Failed) and exit-0 semantics.
        Assert.Equal(SeedStatus.AlreadySeeded, second.Status);
        Assert.True(second.IsSuccess, second.Message);

        var secondManifest = await ReadManifestAsync(factory.ManifestPath);
        var afterSecond = await SnapshotAsync();

        // The anchor was RECOVERED from the day-D data, not re-read from the
        // advanced clock — so the manifest's anchor, fingerprint, and counts are
        // all identical to day D's.
        Assert.True(secondManifest.Complete);
        Assert.Equal(firstManifest.GeneratedAtAnchor, secondManifest.GeneratedAtAnchor);
        Assert.Equal(firstManifest.Fingerprint, secondManifest.Fingerprint);
        Assert.Equal(firstManifest.Counts, secondManifest.Counts);

        // And no shifted duplicates actually landed in the database.
        Assert.Equal(afterFirst, afterSecond);
    }

    private async Task<SeedResult> SeedOnceAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
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

    private static async Task<SimulationManifest> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SimulationManifest>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {path}.");
    }
}
