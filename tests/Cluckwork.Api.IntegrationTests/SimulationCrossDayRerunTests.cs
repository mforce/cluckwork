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
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";
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
    // each test drives the simulation seed runs itself). But re-declaring
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
