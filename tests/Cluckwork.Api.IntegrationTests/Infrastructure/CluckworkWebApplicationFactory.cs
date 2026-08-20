namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Security.Cryptography;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

// Full-stack integration tests run against a real Postgres container (tech spec §5.4).
// SQLite is deliberately NOT used — EF Core SQL semantics differ too much.
public class CluckworkWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Must match the image pinned in deploy/docker-compose.yml and docker-compose.dev.yml —
    // tests have to validate against the same Postgres version prod runs.
    private const string PostgresImage = "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage)
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    // Default true: the suite runs against a migrated schema. A factory can
    // override to false to observe a host booting against an UNMIGRATED database
    // (e.g. the #263 MigrateOnStartup=false skip test).
    protected virtual bool MigrateSchemaOnInitialize => true;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        if (!MigrateSchemaOnInitialize) return;

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
        // Standard OTLP variables may exist in a developer or CI environment. A
        // present blank canonical endpoint selects Cluckwork's disabled profile.
        builder.UseSetting("Otlp:Endpoint", "");
        // The suite logs in and refreshes constantly from one in-process
        // "client"; keep the auth rate limits (#143) out of the way.
        // RateLimitingTests derive a factory that tightens them back down.
        builder.UseSetting("RateLimiting:Login:PermitLimit", "1000000");
        builder.UseSetting("RateLimiting:Refresh:PermitLimit", "1000000");
        // A small logo cap (#123) so the size-boundary tests allocate KB, not
        // megabytes. Well under the 5 MB ceiling, so it validates at startup.
        builder.UseSetting("FarmLogo:MaxUploadBytes", LogoUploadCap.ToString());
        // The Testcontainers DB is a co-located PLAINTEXT Postgres with no fronting
        // proxy, so Production-derived tests opt out of the deploy-config boot guards:
        // #262 (Database:AllowInsecureConnection), #260 (RateLimiting:AllowNoTrustedProxies),
        // and #319 (a concrete AllowedHosts — appsettings defaults to "*", which fails the
        // Production boot). This mirrors the bundled compose reference stack. All three are
        // no-ops in the default "Testing" env (the guards are Production-gated); it's the
        // Production-derived factories (TrustedProxyGuardTests, ConnectionTlsFloorWiringTests,
        // AllowedHostsGuardTests) that need them. A guard-specific test overrides the single
        // setting it exercises.
        builder.UseSetting("Database:AllowInsecureConnection", "true");
        builder.UseSetting("RateLimiting:AllowNoTrustedProxies", "true");
        builder.UseSetting("AllowedHosts", "cluckwork.test");
    }

    // Mirrors the FarmLogo:MaxUploadBytes above so the size tests can size their
    // fixtures against the cap this host actually enforces.
    public const int LogoUploadCap = 64 * 1024;
}

// Ephemeral, test-only RSA credentials: generated once per test process (not
// per WebApplicationFactory — key generation is expensive and the suite spins
// up many factories) and never persisted, so nothing under source control is a
// private-key literal for a secret scanner to normalize. PEM export uses the
// framework's own encoders (RSA.ExportPkcs8PrivateKeyPem / .ExportSubjectPublicKeyInfoPem)
// rather than a hand-rolled ASN.1/PEM writer. The exported PEM is then folded to
// literal "\n" escapes — the same shape deploy/.env.example uses for a real
// Jwt__PrivateKeyPem — so ConfigureWebHost below exercises the real
// PemKey.Normalize() unescape path instead of bypassing it with pre-formatted
// real newlines.
internal static class TestJwtKeys
{
    private static readonly Lazy<(string PrivateKeyPem, string PublicKeyPem)> KeyPair = new(GenerateKeyPair);

    public static string PrivateKeyPem => KeyPair.Value.PrivateKeyPem;

    public static string PublicKeyPem => KeyPair.Value.PublicKeyPem;

    private static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem().ReplaceLineEndings("\\n");
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem().ReplaceLineEndings("\\n");
        return (privateKeyPem, publicKeyPem);
    }
}
