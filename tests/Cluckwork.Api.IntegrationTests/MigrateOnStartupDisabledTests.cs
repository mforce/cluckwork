namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #263 — the PR's CENTRAL guarantee: with Database:MigrateOnStartup=false the
// request-serving process never runs schema DDL at boot; a forgotten migrate job
// is caught by readiness rather than served against a stale schema. The base
// factory normally migrates in InitializeAsync, so this one opts out to leave the
// schema unmigrated and observe the serving host booting against it.
public sealed class NoBootMigrateFactory : CluckworkWebApplicationFactory
{
    // Seeding is ENABLED with real creds so the boot exercises DatabaseSeeder
    // against the unmigrated schema — proving the seeder logs+skips (per its
    // contract) instead of crash-looping the host on a missing table (#263 review).
    public string AdminEmail { get; } = $"nomigrate-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override bool MigrateSchemaOnInitialize => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
    }
}

public sealed class MigrateOnStartupDisabledTests(NoBootMigrateFactory factory)
    : IClassFixture<NoBootMigrateFactory>
{
    private readonly NoBootMigrateFactory _factory = factory;

    [Fact]
    public async Task MigrateOnStartupFalse_DoesNotMigrateOnBoot_StaysUp_AndReadinessReportsUnhealthy()
    {
        // Boots the serving host against the unmigrated schema. Must not throw:
        // the seeder hits a missing table and logs+skips (contract), so boot
        // completes instead of crash-looping.
        var client = _factory.CreateClient();

        // 1. Boot did NOT apply migrations — a table only a migration creates is absent.
        await using (var conn = new NpgsqlConnection(_factory.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('public.\"AspNetUsers\"') IS NULL", conn);
            var notMigrated = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.True(notMigrated, "MigrateOnStartup=false must NOT migrate on a serving boot");
        }

        // 2. The host stayed up (seeder resilience): liveness is green.
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // 3. Readiness reports UNHEALTHY (503) against the un-migrated schema — the
        //    backstop that stops an orchestrator routing traffic to a stale schema.
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // 4. Sanity: EF confirms migrations are pending (nothing applied on boot).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
    }
}
