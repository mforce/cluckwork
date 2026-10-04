using Cluckwork.Api.Configuration;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
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
        builder.Services.AddCluckworkFeatures(builder.Configuration);
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
        Assert.Equal(0, resolutions);
    }
}
