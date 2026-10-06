using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

public sealed class ProvisioningPortFactory : CluckworkWebApplicationFactory;

// #1116: AccountProvisioner stages a new farm's default grades and unit conversions through these two Production
// ports. Each stages for the resolved tenant into the caller's scoped context, only inside the caller's
// transaction, and never saves. AccountProvisioningTests.Provision_WhenIdentityRejectsOwner_RollsBackTheAccount
// covers the rollback when Owner creation fails after they have run.
public sealed class ProvisioningPortTests(ProvisioningPortFactory factory) : IClassFixture<ProvisioningPortFactory>
{
    public static TheoryData<string> Ports => ["grades", "conversions"];

    [Theory]
    [MemberData(nameof(Ports))]
    public async Task StageDefaults_AddsTheResolvedTenantsRowsToTheCallersContext_WithoutSaving(string port)
    {
        var accountId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);

        var (staged, stored) = await AmbientTransaction.RunAsync(db.Database, async (transaction, ct) =>
        {
            Stage(scope.ServiceProvider, port);
            // Read inside the same transaction: a save would show here.
            var result = (StagedAccountIds(db, port), await StoredCountAsync(db, port, accountId));
            await transaction.RollbackAsync(ct);
            return result;
        });

        Assert.Equal(DefaultCount(port, accountId), staged.Count);
        Assert.All(staged, id => Assert.Equal(accountId, id));
        Assert.Equal(0, stored);
    }

    [Theory]
    [MemberData(nameof(Ports))]
    public async Task StageDefaults_RefusesAnUnresolvedTenant(string port)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await AmbientTransaction.RunAsync(db.Database, async (transaction, ct) =>
        {
            Assert.Throws<InvalidOperationException>(() => Stage(scope.ServiceProvider, port));
            await transaction.RollbackAsync(ct);
            return 0;
        });
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [MemberData(nameof(Ports))]
    public void StageDefaults_RefusesToRunOutsideATransaction(string port)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => Stage(scope.ServiceProvider, port));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static void Stage(IServiceProvider services, string port)
    {
        if (port == "grades")
            services.GetRequiredService<IEggGradeProvisioning>().StageDefaults();
        else
            services.GetRequiredService<IEggUnitConversionProvisioning>().StageDefaults();
    }

    private static List<Guid> StagedAccountIds(AppDbContext db, string port) => port == "grades"
        ? [.. db.ChangeTracker.Entries<EggGrade>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.AccountId)]
        : [.. db.ChangeTracker.Entries<EggUnitConversion>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.AccountId)];

    private static int DefaultCount(string port, Guid accountId) => port == "grades"
        ? EggGrade.Defaults(accountId, SeedDefaults.FarmId).Count
        : EggUnitConversion.Defaults(accountId).Count;

    private static Task<int> StoredCountAsync(AppDbContext db, string port, Guid accountId) => port == "grades"
        ? db.EggGrades.IgnoreQueryFilters().AsNoTracking().CountAsync(g => g.AccountId == accountId)
        : db.EggUnitConversions.IgnoreQueryFilters().AsNoTracking().CountAsync(c => c.AccountId == accountId);
}
