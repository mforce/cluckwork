using Cluckwork.Api.Configuration;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Hosting.Modules;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccessSeedDependencyTests(CluckworkWebApplicationFactory factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DemoSeederReadGraphNeverConstructsIdentityOrRedis(bool poisonRedis)
    {
        var email = $"dependency-owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.AccountId == accountId && u.Email == email).Select(u => u.Id).SingleAsync());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.ConnectionString,
            ["Database:AllowInsecureConnection"] = "true",
            ["SharedState:Redis:ConnectionString"] = "127.0.0.1:1",
        });
        builder.Services.AddCluckworkPersistence(builder.Configuration, builder.Environment);
        builder.Services.AddCluckworkIdentity(builder.Configuration, ProcessRole.OneShot);
        builder.Services.AddCluckworkSharedState(builder.Configuration, ProcessRole.OneShot);
        builder.Services.AddCluckworkModules(builder.Configuration);
        builder.Services.AddCluckworkJobs();
        var resolutions = 0;
        if (poisonRedis)
        {
            builder.Services.RemoveAll<IConnectionMultiplexer>();
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                resolutions++;
                throw new InvalidOperationException("demo must not construct Redis");
            });
        }
        else
        {
            builder.Services.RemoveAll<IIdentityProvider>();
            builder.Services.AddScoped<IIdentityProvider>(_ =>
            {
                resolutions++;
                throw new InvalidOperationException("demo must not construct Identity");
            });
        }
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var error = Record.Exception(() => scope.ServiceProvider.GetRequiredService<DemoDataSeeder>());
        Assert.Null(error);
        var lookup = scope.ServiceProvider.GetRequiredService<IAccessSeedLookup>();
        var actor = await lookup.GetActorAsync(accountId, userId);
        Assert.NotNull(actor);
        Assert.Equal(userId, actor.Id);
        Assert.Equal(userId, (await lookup.FindUserByEmailAsync(accountId, email))?.Id);
        Assert.Contains(await lookup.ListUsersInRoleAsync(accountId, Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner),
            member => member.Id == userId);
        Assert.Equal(0, resolutions);
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Testing", true)]
    public async Task FixtureReadPortIsRegisteredOnlyOutsideProduction(string environment, bool expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.ConnectionString,
            ["Database:AllowInsecureConnection"] = "true",
        });
        builder.Services.AddCluckworkPersistence(builder.Configuration, builder.Environment);
        builder.Services.AddCluckworkIdentity(builder.Configuration, ProcessRole.OneShot);
        builder.Services.AddCluckworkSharedState(builder.Configuration, ProcessRole.OneShot);
        builder.Services.AddCluckworkModules(builder.Configuration);
        builder.Services.AddCluckworkJobs();
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        Assert.Equal(expected, scope.ServiceProvider.GetService<IAccessSeedLookup>() is not null);
        Assert.Equal(expected, scope.ServiceProvider.GetService<DemoDataSeeder>() is not null);
        Assert.Equal(expected, scope.ServiceProvider.GetService<SimulationDataSeeder>() is not null);
    }
}
