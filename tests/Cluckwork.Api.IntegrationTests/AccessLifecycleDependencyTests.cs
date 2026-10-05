using Cluckwork.Api.Cli;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Hosting.Modules;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccessLifecycleDependencyTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task ProvisioningOutcomeNamesTheCommittedFarmAndOwnerAndCarriesTheirWorkingPassword()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var slug = "outcome-" + suffix[..12];
        var email = $"outcome-{suffix}@test.local";
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<IAccessOperations>();
        var result = await operations.ProvisionAccountAsync(
            "Outcome Farm", slug, email, "en-US", "USD", "UTC", CancellationToken.None);
        Assert.True(result.IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Slug == slug);
        var owner = await db.Users.SingleAsync(u => u.AccountId == account.Id && u.Email == email);
        Assert.Equal(account.Id, result.Value.AccountId);
        Assert.Equal(account.Slug, result.Value.Slug);
        Assert.Equal(owner.Email, result.Value.OwnerEmail);
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await factory.TryLoginAsync(email, result.Value.TemporaryPassword)).StatusCode);
    }

    [Theory]
    [InlineData("suspend-account", false)]
    [InlineData("reactivate-account", false)]
    [InlineData("rename-account", false)]
    [InlineData("suspend-account", true)]
    [InlineData("reactivate-account", true)]
    [InlineData("rename-account", true)]
    public async Task RealLifecycleVerbNeverConstructsIdentityRedisOrUserManager(string verb, bool poisonRedis)
    {
        var email = $"closure-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = await factory.FarmCodeForAsync(email);
        var builder = WebApplication.CreateBuilder();
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
        var resolutions = 0;
        builder.Services.RemoveAll<UserManager<ApplicationUser>>();
        builder.Services.AddScoped<UserManager<ApplicationUser>>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("UserManager must not be constructed");
        });
        if (poisonRedis)
        {
            builder.Services.RemoveAll<IConnectionMultiplexer>();
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                resolutions++;
                throw new InvalidOperationException("Redis must not be constructed");
            });
        }
        else
        {
            builder.Services.RemoveAll<IIdentityProvider>();
            builder.Services.AddScoped<IIdentityProvider>(_ =>
            {
                resolutions++;
                throw new InvalidOperationException("Identity must not be constructed");
            });
        }
        await using var app = builder.Build();
        ICliCommand command = verb switch
        {
            "suspend-account" => new SuspendAccountCliCommand(),
            "reactivate-account" => new ReactivateAccountCliCommand(),
            _ => new RenameAccountCliCommand(),
        };
        var exit = await command.RunAsync(app,
            [verb, "--slug", slug, "--new-slug", "renamed-" + accountId.ToString("N")[..12], "--reason", "dependency proof"]);
        Assert.Equal(0, resolutions);
        Assert.Equal(0, exit);
    }
}
