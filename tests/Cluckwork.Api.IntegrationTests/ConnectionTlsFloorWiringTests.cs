namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #262 — proves the Production TLS floor is WIRED into startup (Program.cs calls
// PostgresConnectionString.NormalizeAndValidate once, before Build, with isProduction and
// the AllowInsecureConnection opt-out). The floor's LOGIC is unit-tested in
// PostgresConnectionStringTests; this locks the end-to-end wiring — deleting or mis-wiring
// the Program.cs call would leave every helper unit test green but fail these boot tests
// (mirrors SeedTimeZoneTests' boot-wiring approach).
//
// Own Postgres container: Production config differs from the shared "Testing" factory, and
// the connection is the plaintext Testcontainers one (sslmode unset -> Prefer -> weak).
public sealed class ProductionInsecureDbFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Real Production env is what flips isProduction=true in Program.cs (the floor only
        // enforces there). The base factory connects plaintext via Testcontainers AND provides
        // both Production boot-guard opt-outs (#262 AllowInsecureConnection, #260
        // AllowNoTrustedProxies), so this host boots and the fixture migration runs. Case (a)
        // below flips ONLY AllowInsecureConnection off to observe the #262 floor firing.
        builder.UseEnvironment("Production");
    }
}

public sealed class ConnectionTlsFloorWiringTests : IClassFixture<ProductionInsecureDbFactory>
{
    private readonly ProductionInsecureDbFactory _factory;

    public ConnectionTlsFloorWiringTests(ProductionInsecureDbFactory factory) => _factory = factory;

    [Fact]
    public void Production_PlaintextConnection_WithOptOut_Boots()
    {
        // (b) Production + plaintext + Database:AllowInsecureConnection=true → boots.
        var boot = Record.Exception(() => _factory.CreateClient());

        Assert.Null(boot);
    }

    [Fact]
    public void Production_PlaintextConnection_WithoutOptOut_FailsBootWithTlsFloorMessage()
    {
        // (a) Production + plaintext + no opt-out → boot FAILS at configuration time (before
        // Build, before the DB is touched) with the actionable TLS-floor message.
        using var badHost = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Database:AllowInsecureConnection", "false"));

        var boot = Record.Exception(() => badHost.CreateClient());

        Assert.NotNull(boot);
        var message = Flatten(boot!);
        Assert.Contains("TLS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sslmode", message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            parts.Add(e.Message);
        return string.Join(" | ", parts);
    }
}
