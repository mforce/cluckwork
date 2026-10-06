using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccessSeedLookupTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task SeedReadsPropagateCancellation()
    {
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.GetActorAsync(Guid.NewGuid(), Guid.NewGuid(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.FindUserByEmailAsync(Guid.NewGuid(), "missing@test.local", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lookup.ListUsersInRoleAsync(Guid.NewGuid(), Roles.Owner, canceled.Token));
    }

    [Fact]
    public async Task ActorReadsPreserveAccountDisabledStateAndEveryActualRole()
    {
        var email = $"shared-actor-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync(email);
        var accountB = await factory.SeedAccountWithUserAsync(email);
        Guid actorA;
        var disabledAt = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountA);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actor = await db.Users.SingleAsync(u => u.AccountId == accountA && u.Email == email);
            actorA = actor.Id;
            actor.DisabledAt = disabledAt;
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRolesAsync(actor, [Roles.Manager, Roles.ReadOnly])).Succeeded);
        }
        using var read = factory.Services.CreateScope();
        var lookup = read.ServiceProvider.GetRequiredService<IAccessSeedLookup>();
        var inA = await lookup.FindUserByEmailAsync(accountA, email.ToUpperInvariant());
        var inB = await lookup.FindUserByEmailAsync(accountB, email);
        Assert.NotNull(inA);
        Assert.NotNull(inB);
        Assert.Equal(actorA, inA.Id);
        Assert.NotEqual(inA.Id, inB.Id);
        Assert.Equal(email, inA.Email);
        Assert.NotNull(inA.DisabledAt);
        Assert.Null(inB.DisabledAt);
        Assert.Equal(new[] { Roles.Owner, Roles.Manager, Roles.ReadOnly }.Order(StringComparer.Ordinal),
            inA.Roles.Order(StringComparer.Ordinal));
        Assert.Equal([Roles.Owner], inB.Roles);
        Assert.Null(await lookup.GetActorAsync(accountB, actorA));
        Assert.Null(await lookup.GetActorAsync(accountA, Guid.NewGuid()));
        var reread = await lookup.GetActorAsync(accountA, actorA);
        Assert.NotNull(reread);
        Assert.Equal(inA.Roles.Order(StringComparer.Ordinal), reread.Roles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RoleMembersIncludeDisabledUsersAreAccountScopedAndOrderedById()
    {
        var email = $"role-owner-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(email);
        var otherAccount = await factory.SeedAccountWithUserAsync(email);
        await factory.SeedUserAsync(account, $"second-{Guid.NewGuid():N}@test.local", Roles.Owner);
        var expected = await factory.WithTenantScopeAsync(account, async db =>
        {
            var first = await db.Users.SingleAsync(u => u.AccountId == account && u.Email == email);
            first.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return await db.Users.Where(u => u.AccountId == account).OrderBy(u => u.Id).Select(u => u.Id).ToArrayAsync();
        });
        using var scope = factory.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>();
        var members = await lookup.ListUsersInRoleAsync(account, Roles.Owner);
        Assert.Equal(expected, members.Select(u => u.Id));
        Assert.Single(members, u => u.DisabledAt is not null);
        var foreign = Assert.Single(await lookup.ListUsersInRoleAsync(otherAccount, Roles.Owner));
        Assert.DoesNotContain(foreign.Id, members.Select(u => u.Id));
    }

    [Fact]
    public async Task EmailReadUsesTheRegisteredIdentityNormalizer()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILookupNormalizer>();
            services.AddSingleton<ILookupNormalizer, PrefixedEmailNormalizer>();
        }));
        var account = await factory.SeedAccountWithUserAsync($"normalizer-owner-{Guid.NewGuid():N}@test.local");
        var email = $"case-{Guid.NewGuid():N}@test.local";
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(account);
        var user = new ApplicationUser { Id = Guid.NewGuid(), AccountId = account, UserName = email, Email = email };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .CreateAsync(user, TestHarness.Password)).Succeeded);
        var found = await scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>()
            .FindUserByEmailAsync(account, email.ToUpperInvariant());
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Manager)]
    public async Task FixtureCreationAuthenticatesWithoutPasswordChangeAndWritesOnce(string? role)
    {
        var ownerEmail = $"cast-owner-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(ownerEmail);
        var email = $"cast-user-{Guid.NewGuid():N}@test.local";
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(account);
            var actor = await scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>()
                .FindUserByEmailAsync(account, ownerEmail);
            Assert.NotNull(actor);
            scope.ServiceProvider.GetRequiredService<CurrentUserContext>().Resolve(actor.Id, actor.Email!, actor.Roles);
            var operations = scope.ServiceProvider.GetRequiredService<IAccessOperations>();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                operations.CreateUserAsync(account, "canceled-" + email, TestHarness.Password,
                    role, "Cast User", canceled.Token));
            var created = await operations.CreateUserAsync(
                account, email, TestHarness.Password, role, "Cast User", CancellationToken.None);
            Assert.True(created.IsSuccess);
            userId = created.Value;
            var actual = await scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>().GetActorAsync(account, userId);
            Assert.NotNull(actual);
            Assert.Equal(role is null ? [] : new[] { role }, actual.Roles);
        }
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(1, await factory.WithTenantScopeAsync(account, db =>
            db.Users.CountAsync(u => u.AccountId == account && u.Email == email)));
        Assert.Equal(1, await factory.WithTenantScopeAsync(account, db =>
            db.AuditEvents.CountAsync(a => a.Action == "User.Create" && a.EntityId == userId)));
    }

    private sealed class PrefixedEmailNormalizer : ILookupNormalizer
    {
        private readonly UpperInvariantLookupNormalizer standard = new();
        public string? NormalizeName(string? name) => standard.NormalizeName(name);
        public string? NormalizeEmail(string? email) => email is null ? null : "lookup:" + standard.NormalizeEmail(email);
    }
}
