namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #264 — the default account is provisioned from Seed:TimeZoneId (not a hard
// "UTC" literal), and an unresolvable configured zone fails the boot loudly.
// Own container: the base seed writes to the fixed SeedDefaults.AccountId.
public sealed class ManilaSeedFactory : CluckworkWebApplicationFactory
{
    // Runtime-generated — never a hardcoded credential (GitGuardian scans PRs).
    public string AdminEmail { get; } = $"tzseed-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Seed:Enabled", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:TimeZoneId", "Asia/Manila");
    }
}

public sealed class SeedTimeZoneTests : IClassFixture<ManilaSeedFactory>
{
    private readonly ManilaSeedFactory _factory;

    public SeedTimeZoneTests(ManilaSeedFactory factory) => _factory = factory;

    [Fact]
    public async Task DefaultAccount_IsSeededWithTheConfiguredTimeZone()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters()
            .SingleAsync(a => a.Id == SeedDefaults.AccountId);
        Assert.Equal("Asia/Manila", account.TimeZoneId);
    }

    [Fact]
    public void AnUnresolvableSeedTimeZone_FailsTheBoot_NotTheFirstRequest()
    {
        // The guard's logic is unit-tested (TimeZoneAvailabilityTests); this proves
        // it is WIRED into startup — deleting the Program.cs call would leave the
        // unit test green (mirrors FarmLogoTests' boot-wiring test). The eager
        // check runs before the DB is even touched, so the bad host fails without
        // seeding anything.
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Seed:TimeZoneId", "Not/AZone"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        Assert.Contains("Not/AZone", message);
        Assert.Contains("tz data", message);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }
}
