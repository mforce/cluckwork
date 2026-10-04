using Cluckwork.Api.Cli;
using Cluckwork.Api.Configuration;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Cluckwork.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class AccessLifecycleDependencyTests(CluckworkWebApplicationFactory factory)
{
    [Theory]
    [InlineData("suspend-account", false)]
    [InlineData("reactivate-account", false)]
    [InlineData("rename-account", false)]
    [InlineData("suspend-account", true)]
    [InlineData("reactivate-account", true)]
    [InlineData("rename-account", true)]
    public async Task RealLifecycleVerbNeverConstructsIdentityOrRedis(string verb, bool poisonRedis)
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
        builder.Services.AddCluckworkFeatures(builder.Configuration);
        var resolutions = 0;
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
