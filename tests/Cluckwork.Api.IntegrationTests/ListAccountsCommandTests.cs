namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using Cluckwork.Api.Cli;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

    // #560 (codex round 2) — the account name is tenant-controlled free text
    // whose validator bounds only length, so it can carry CR/LF/tab/ANSI. Unit
    // check of the strip itself.
    [Theory]
    [InlineData("Green Valley Farm", "Green Valley Farm")]
    [InlineData("with\ttab", "with tab")]
    [InlineData("line\nbreak", "line break")]
    [InlineData("carriage\r\nreturn", "carriage  return")]
    [InlineData("esc\u001b[31mred", "esc [31mred")]
    public void SanitizeForDisplay_ReplacesControlCharactersWithSpaces(string input, string expected) =>
        Assert.Equal(expected, ListAccountsCliCommand.SanitizeForDisplay(input));

    [Fact]
    public async Task ListAccounts_StripsControlCharactersFromTenantNames_SoOneFarmCannotForgeAnothersRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"inject-{Guid.NewGuid():N}@test.local");
        var seededSlug = "farm-" + accountId.ToString("N")[..12];
        // A malicious name: an embedded newline would forge a standalone row and
        // the ANSI escape would rewrite the terminal. Written straight to the
        // column (parameterised, so the control bytes land as data, not SQL).
        var injected = "row-a\u001b[31m\trow-b\nFORGED-ROW";
        await factory.WithTenantScopeAsync(accountId, db => db.Database.ExecuteSqlAsync(
            $"UPDATE \"Accounts\" SET \"Name\" = {injected} WHERE \"Id\" = {accountId}"));

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            StartListAccounts(), SubprocessTimeout);

        Assert.True(0 == exitCode, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        // No raw ESC survives, and the embedded newline did not forge a line:
        // "FORGED-ROW" stays on the seeded account's own row (which starts with
        // its slug) rather than becoming a line of its own.
        Assert.DoesNotContain('\u001b', stdout);
        Assert.DoesNotContain(
            stdout.Split('\n'), line => line.StartsWith("FORGED-ROW", StringComparison.Ordinal));
        Assert.Contains(seededSlug, stdout);
    }
}
