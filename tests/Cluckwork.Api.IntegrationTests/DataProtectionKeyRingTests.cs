using System.Security.Cryptography;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Hosting.Modules;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Cluckwork.Api.IntegrationTests;

// #794 — every serving replica shares one key ring through the database.
public sealed class DataProtectionKeyRingTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    private const string Purpose = "Cluckwork.IntegrationTests.794";

    [Fact]
    public async Task A_payload_protected_on_one_host_unprotects_on_another_sharing_only_the_database()
    {
        // Separate service provider, so nothing in memory is shared, and a different
        // default discriminator, standing in for a replica deployed at another path.
        await using var second = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IApplicationDiscriminator>(new FixedDiscriminator("/elsewhere"))));

        var protectedPayload = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Protect("password-reset");

        var unprotected = second.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Unprotect(protectedPayload);

        Assert.Equal("password-reset", unprotected);

        using var scope = factory.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DataProtectionKeys.Select(k => k.Xml!).ToListAsync();
        var key = Assert.Single(keys);
        Assert.Contains("<EncryptedData", key, StringComparison.Ordinal);
        Assert.DoesNotContain("<value>", key, StringComparison.Ordinal);
    }

    // A one-shot verb mints and redeems in one process, so its ring needs no
    // database and must persist nowhere: a second one-shot process cannot read it.
    [Fact]
    public void A_one_shot_ring_round_trips_in_memory_and_persists_nowhere()
    {
        using var first = OneShotProvider();
        using var second = OneShotProvider();
        var protector = first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        var protectedPayload = protector.Protect("recover-admin");

        Assert.Equal("recover-admin", protector.Unprotect(protectedPayload));
        Assert.Throws<CryptographicException>(() =>
            second.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedPayload));
    }

    private static ServiceProvider OneShotProvider()
    {
        var services = new ServiceCollection();
        services.AddCluckworkDataProtection(
            new ConfigurationBuilder().Build(),
            new HostingEnvironment { EnvironmentName = Environments.Production },
            ProcessRole.OneShot);
        return services.BuildServiceProvider();
    }

    private sealed class FixedDiscriminator(string discriminator) : IApplicationDiscriminator
    {
        public string Discriminator => discriminator;
    }
}
