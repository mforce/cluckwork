namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

// Full-stack integration tests run against a real Postgres container (tech spec §5.4).
// SQLite is deliberately NOT used — EF Core SQL semantics differ too much.
public sealed class CluckworkWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": that would load the developer's user-secrets (e.g.
        // Seed:* credentials) into the test host and seed machine-specific users
        // into the test database — tests must be hermetic.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Postgres");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:PrivateKeyPem", TestJwtKeys.PrivateKeyPem);
        builder.UseSetting("Jwt:PublicKeyPem", TestJwtKeys.PublicKeyPem);
        builder.UseSetting("Jwt:Issuer", "cluckwork-test");
        builder.UseSetting("Jwt:Audience", "cluckwork-api-test");
    }
}

internal static class TestJwtKeys
{
    public const string PrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDCPCiFHUUz+ChW
        aR4GWL0GwMzfvMNETwraIh7UlnyRfd2+LJHkU7xteuqMcgTqQOfqKYNCPDHs9irr
        n4FCAlTbYYRSx5K7f0XuIQPTRIOspfJ0yuQmsGNfVAHIWZ/U09wxeEIqOD5oy9+7
        PhYuF/Eu/YdsQMSb5FX/PicmwFMGeWdjNb73gOSI1Djrc1Yuy+HxjISPu104T80w
        DhtaVBEYn9jMRQfpeThFIh+f2MoayQGQhH23HomEuSn0EN7A94SRpcUSxJbDns86
        ne3ZpZx3a3krqyks8UD+q5519XdPTM6ZDuxjWp9agx/bsI2apW3I1mIc9RFzjrZa
        cd/omxNRAgMBAAECggEAC9/CfKJFo3Pgj+lG0I2i1yeI0sXy9EFmhP+sXbPKL1kH
        6fcGr3QaxyDaNz4mbKVthBI3++/q/6zsImnGrLDQrBVr4eVhVBDnlxNZvPPxnoep
        0P/hBxg2B/suTXekqq2ttrC+zz7PWmfZwGQvFg/owrYVLf4O4uaYZwVfPnpIDRN6
        KtfDu4Zu+44axie6CajY70onjRQHSUv3Jrx4xjxoN/i1iWWQPmnxubMRG1Tx4eQf
        4cKYy3iiENHKei/L2MY/ptzNudPFvfkqKcFxnzelCaYE7irKFhM5vLs3V76UWFV+
        VvBtxelL8y3Uya2bc3Pr52A6lBWDsHPJ1zxm/AFSoQKBgQDzfk3lyoT3YlvpHUbL
        q7CFHg755F239DmNFy6sESourCwfNOPmAbY7fEUjA6k75H2MkHd3pcEhBEBldUID
        cPQYHXTe/EMrpNdPRa79yDHMuF3E4sNX31sqzgbVRx3wQ0pwPr4pC4SZE+j9tG6/
        cmkMOil/Ne3a362Q5KNSx7eTMQKBgQDMNie+o89q4XKNTDsnOB5xkGF1AmWKocvG
        CF8YuUuoMnZFuBKIsHmANDQqtuIsobKMtZyuVSoUmI3d2hqFUYRQHMQ9cF9+Gx+G
        guXlaj2M7lqQQT1kQ2MBQSTlG8oflT0dauhYdA/ogl4vG59bxPjGan00vBdhtdqu
        lf/dphg6IQKBgQConD/I7hJhVEUdCd7qTnuv0n7AYHjdV0s6/mCdWk2BgEwVWASw
        U2Mjkgw2EOTxyml+GtP/kFJKUK1fFHGf/GmrAUra1oiVAlLuW+yvZB/ICas1GWn3
        wX1aCM0Gh3ad15sGWwxHU+iAMB9Y/8bo34sKooP1yRxqQhXojcrjGdVvIQKBgA2n
        eVTX4yCEXoJwHGxs5iw1uS53sI5qbxOYr7MZgKOIbDwRKLwXAKi/1NUeUVUmoqeh
        5Q4LB7tE0AeLc8aCQtSQd9ab0ua9rYfy7KhASElKDqgilJZFozMMRglDqGogMmvr
        IAn6CK5FOULxF+Cs9O1fZWvHP9D6tdqCkQ8i8e/BAoGBAJQPubB/gp+0XqLWpSjx
        MmI2QAUDuZVVIa6eDf7qzue4aUbAdg8IaAEiQw/Wxs6GCCpJ1wLbbDncp5LlfRKM
        TudOrIQI+TFoyyvij3FvH3enzNqbtIvHGZ+PIoRJRs5NxPXRcfRDN5fR4dbUpIKR
        PyfFfNZzwIj8nMoGNcfb8Om1
        -----END PRIVATE KEY-----
        """;

    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwjwohR1FM/goVmkeBli9
        BsDM37zDRE8K2iIe1JZ8kX3dviyR5FO8bXrqjHIE6kDn6imDQjwx7PYq65+BQgJU
        22GEUseSu39F7iED00SDrKXydMrkJrBjX1QByFmf1NPcMXhCKjg+aMvfuz4WLhfx
        Lv2HbEDEm+RV/z4nJsBTBnlnYzW+94DkiNQ463NWLsvh8YyEj7tdOE/NMA4bWlQR
        GJ/YzEUH6Xk4RSIfn9jKGskBkIR9tx6JhLkp9BDewPeEkaXFEsSWw57POp3t2aWc
        d2t5K6spLPFA/quedfV3T0zOmQ7sY1qfWoMf27CNmqVtyNZiHPURc462WnHf6JsT
        UQIDAQAB
        -----END PUBLIC KEY-----
        """;
}
