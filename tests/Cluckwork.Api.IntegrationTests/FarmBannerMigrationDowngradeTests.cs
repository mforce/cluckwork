namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

// Codex review of #496 (AddFarmBannerColumns): the pre-banner schema required
// every FarmLogos row to carry a real logo, so a banner-only row (Content
// NULL, BannerContent set) that this migration's Up() allows has no valid
// pre-migration state. Down() used to backfill every NULL Content to an empty
// bytea and then reinstate ck_farm_logos_content_length ("> 0"), which an
// empty bytea immediately violates — the downgrade threw instead of
// completing. Same pattern as BaseReferenceDataMigrationTests: drive EF's
// migrator directly against a throwaway Postgres, no WebApplicationFactory.
public sealed class FarmBannerMigrationDowngradeTests
{
    private const string PostgresImage =
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private const string PreviousMigrationId = "20260808004059_AddDailyEntryStepperUnitPreferences";

    private static AppDbContext BuildContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, connectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(new TenantContext()));
        return new AppDbContext(options.Options, new TenantContext());
    }

    [Fact]
    public async Task DowngradingPastAddFarmBannerColumns_DeletesBannerOnlyRows_InsteadOfFailing()
    {
        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await using var db = BuildContext(postgres.GetConnectionString());
        await db.Database.MigrateAsync();

        var bannerOnly = FarmLogo.Create(Guid.NewGuid(), SeedDefaults.AccountId, SeedDefaults.FarmId);
        bannerOnly.ReplaceBanner(
            new SanitizedImage(ImageKind.Png, [1, 2, 3], Width: 10, Height: 20), DateTimeOffset.UtcNow);
        db.FarmLogos.Add(bannerOnly);
        await db.SaveChangesAsync();

        var migrator = db.Database.GetService<IMigrator>();

        // Would throw DbUpdateException / PostgresException on the reinstated
        // check constraint before the fix.
        await migrator.MigrateAsync(PreviousMigrationId);

        var remaining = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"FarmLogos\"")
            .FirstAsync();
        Assert.Equal(0, remaining);
    }
}
