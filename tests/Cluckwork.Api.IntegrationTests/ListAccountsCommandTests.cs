namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #531 — `list-accounts` is a real CLI dispatch branch, never exercised by
// WebApplicationFactory (which passes empty args). This spawns the actual built
// Cluckwork.Api.dll as a subprocess — the binary an operator runs — against the
// shared, base-seeded integration database. Read-only, so it safely shares that
// database with the rest of the collection.
//
// The load-bearing assertion is CROSS-TENANT visibility: the verb runs with NO
// tenant resolved, so without IgnoreQueryFilters() the account query filter
// would return ZERO rows. Seeing BOTH the default account and a freshly-seeded
// second account proves the read reaches across the filter.
[Collection(IntegrationCollection.Name)]
public sealed class ListAccountsCommandTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    private Process StartListAccounts()
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" list-accounts")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Default"] = factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        // The Testcontainers DB is plaintext; opt out of the #262 Production TLS
        // floor. The #260/#319 serving guards skip a one-shot verb (#347).
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task ListAccounts_PrintsEveryAccountAcrossTheTenantFilter()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"list-{Guid.NewGuid():N}@test.local");
        var seededSlug = "farm-" + accountId.ToString("N")[..12];

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            StartListAccounts(), SubprocessTimeout);

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        // The default account (backfilled) and the seeded second account are
        // both visible only because the read ignores the (unresolved) tenant
        // filter.
        Assert.Contains("default-farm", stdout);
        Assert.Contains(seededSlug, stdout);
    }
}
