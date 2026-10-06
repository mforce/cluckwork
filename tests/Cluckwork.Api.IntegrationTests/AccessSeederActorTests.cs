using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Modules.Farm.Accounts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

public sealed class AccessSeederActorFactory : CluckworkWebApplicationFactory;

[Collection(DemoSeedCollection.Name)]
public sealed class AccessSeederActorTests(AccessSeederActorFactory factory)
    : IClassFixture<AccessSeederActorFactory>
{
    [Fact]
    public async Task DemoSelectsTheLowestActiveOwnerAndResolvesTheirEntireRoleSet()
    {
        var disabled = new Guid("00000000-0000-0000-0000-000000000001");
        var selected = new Guid("00000000-0000-0000-0000-000000000002");
        var later = new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        foreach (var (id, isDisabled) in new[] { (disabled, true), (selected, false), (later, false) })
        {
            using var create = factory.Services.CreateScope();
            create.ServiceProvider.GetRequiredService<TenantContext>().Resolve(SeedDefaults.AccountId);
            var users = create.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = id,
                AccountId = SeedDefaults.AccountId,
                Email = $"owner-{id:N}@test.local",
                UserName = $"owner-{id:N}@test.local",
                DisabledAt = isDisabled ? DateTimeOffset.UtcNow : null,
            };
            Assert.True((await users.CreateAsync(user, TestHarness.Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Owner)).Succeeded);
            if (id == selected)
                Assert.True((await users.AddToRoleAsync(user, Roles.ReadOnly)).Succeeded);
        }
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
        Assert.Equal(SeedStatus.Seeded, result.Status);
        var actor = scope.ServiceProvider.GetRequiredService<CurrentUserContext>();
        Assert.Equal(selected, actor.UserId);
        Assert.Equal(new[] { Roles.Owner, Roles.ReadOnly }.Order(StringComparer.Ordinal),
            actor.Roles.Order(StringComparer.Ordinal));
        var rows = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, db =>
            db.AuditEvents.Where(a => a.AccountId == SeedDefaults.AccountId).ToListAsync());
        Assert.Equal(977, rows.Count);
        Assert.All(rows, row => Assert.Equal(selected, row.ActorUserId));
    }
}
