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
//
// #283 — there is no longer a runtime seeder to prove resilient against a
// missing table: base provisioning ships in the migrations themselves, and
// nothing else touches the database at boot. This test now proves the
// simpler, stronger claim directly — the serving process boots and stays up
// against an unmigrated schema with NO Seed:* config at all.
public sealed class NoBootMigrateFactory : CluckworkWebApplicationFactory
{
    protected override bool MigrateSchemaOnInitialize => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:MigrateOnStartup", "false");
    }
}

public sealed class MigrateOnStartupDisabledTests(NoBootMigrateFactory factory)
    : IClassFixture<NoBootMigrateFactory>
{
    private readonly NoBootMigrateFactory _factory = factory;

    [Fact]
    public async Task MigrateOnStartupFalse_DoesNotMigrateOnBoot_StaysUp_AndReadinessReportsUnhealthy()
    {
        // Boots the serving host against the unmigrated schema. Must not throw.
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

        // 2. The host stayed up: liveness is green.
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
