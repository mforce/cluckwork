namespace Cluckwork.Api.IntegrationTests;

using System.Diagnostics;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection(IntegrationCollection.Name)]
public sealed class ProvisionAccountCommandTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ProvisionAccount_UnderDmlOnlyRole_PrintsOneWorkingPasswordAfterCommit()
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"cli-{suffix}";
        var email = $"cli-owner-{suffix}@example.test";
        var runtimeConnection = await DmlOnlyRole.CreateConnectionStringAsync(factory.ConnectionString);

        var process = Start(
            $"provision-account --name \"CLI Farm\" --slug {slug} --owner-email {email} "
            + "--locale es-MX --currency MXN",
            runtimeConnection);
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            process, SubprocessTimeout, "provision-account did not exit");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Equal(string.Empty, stderr);
        Assert.Contains($"Farm code: {slug}", stdout);
        Assert.Contains(email, stdout);
        var password = ExtractTemporaryPassword(stdout);
        Assert.Equal(1, CountOccurrences(stdout, password));
        Assert.DoesNotContain(
            stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.TrimStart().StartsWith('[') && line.Contains(password));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Slug == slug);
        Assert.Equal("UTC", account.TimeZoneId);
        Assert.Equal("es-MX", account.Locale);
        Assert.Equal("MXN", account.DefaultCurrencyCode);
        Assert.Equal(email, await db.Users.Where(user => user.AccountId == account.Id)
            .Select(user => user.Email)
            .SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, password)).StatusCode);
    }

    [Theory]
    // #603 made --timezone real, so the rejected spellings here are the ones
    // that are still NOT options. --time-zone stays listed deliberately: it is
    // the near-miss a hand-typed command actually produces, and an unknown
    // option must fail rather than being silently ignored.
    [InlineData("--time-zone America/Los_Angeles")]
    [InlineData("--timezone")]
    [InlineData("--timezone America/Los_Angeles --timezone Asia/Manila")]
    [InlineData("--locale en-US --locale es-MX")]
    [InlineData("--currency")]
    public async Task ProvisionAccount_RejectsMalformedOptionsWithoutWrites(string malformedOptions)
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"bad-cli-{suffix}";
        var email = $"bad-cli-owner-{suffix}@example.test";
        var process = Start(
            $"provision-account --name \"Bad CLI Farm\" --slug {slug} --owner-email {email} {malformedOptions}",
            factory.ConnectionString);

        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            process, SubprocessTimeout, "malformed provision-account did not exit");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("Farm code:", stdout);
        Assert.DoesNotContain("Temporary password:", stdout);
        Assert.Contains("Provisioning failed:", stderr);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Accounts.IgnoreQueryFilters().AnyAsync(account => account.Slug == slug));
    }

    // A zone the #264 clock cannot resolve is refused BEFORE any write. This is
    // a separate test from the malformed-option theory above on purpose: option
    // SHAPE errors are caught before the farm code is echoed, while a semantic
    // rejection happens in the provisioner after it — the same place an invalid
    // --locale is caught. What matters is that nothing was written, so that is
    // what this asserts, rather than the absence of the echo.
    [Theory]
    [InlineData("Mars/Olympus_Mons")] // syntactically plausible, does not exist
    [InlineData("PST")] // an abbreviation, not an IANA id
    [InlineData("asia/manila")] // right zone, wrong case — ids are case-sensitive
    public async Task ProvisionAccount_WithUnresolvableTimezone_WritesNothing(string zone)
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"badtz-{suffix}";
        var email = $"badtz-owner-{suffix}@example.test";

        var process = Start(
            $"provision-account --name \"Bad TZ Farm\" --slug {slug} --owner-email {email} "
            + $"--timezone {zone}",
            factory.ConnectionString);
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            process, SubprocessTimeout, "malformed provision-account did not exit");

        Assert.Equal(1, exitCode);
        Assert.Contains("Provision.TimeZoneInvalid", stderr);
        Assert.DoesNotContain("Temporary password:", stdout);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Accounts.IgnoreQueryFilters().AnyAsync(account => account.Slug == slug));
    }

    // #603 — the zone is committed by the provisioning run itself. Before this,
    // every farm started in UTC and its first day of data was recorded against
    // the wrong zone until the Owner reached Settings.
    [Fact]
    public async Task ProvisionAccount_WithTimezone_CommitsThatZoneOnTheAccount()
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"tz-{suffix}";
        var email = $"tz-owner-{suffix}@example.test";

        var process = Start(
            $"provision-account --name \"TZ Farm\" --slug {slug} --owner-email {email} "
            + "--timezone Asia/Manila",
            factory.ConnectionString);
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            process, SubprocessTimeout, "provision-account did not exit");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Slug == slug);
        Assert.Equal("Asia/Manila", account.TimeZoneId);
        // The zone the farm now holds must be one the #264 clock can actually
        // resolve — a committed-but-unresolvable zone renders no dates at all.
        Assert.Equal("Asia/Manila", TimeZoneInfo.FindSystemTimeZoneById(account.TimeZoneId).Id);
    }

    // Omitting the flag must keep the pre-#603 behaviour exactly: UTC, then the
    // Owner picks the real zone in Settings.
    [Fact]
    public async Task ProvisionAccount_WithoutTimezone_StillStartsInUtc()
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"notz-{suffix}";
        var email = $"notz-owner-{suffix}@example.test";

        var process = Start(
            $"provision-account --name \"No TZ Farm\" --slug {slug} --owner-email {email}",
            factory.ConnectionString);
        var (exitCode, stdout, stderr) = await SeedCommandRunner.RunToCompletionAsync(
            process, SubprocessTimeout, "provision-account did not exit");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().SingleAsync(a => a.Slug == slug);
        Assert.Equal("UTC", account.TimeZoneId);
    }

    private static Process Start(string arguments, string connectionString)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__Default"] = connectionString;
        info.Environment["Database__Provider"] = "Postgres";
        info.Environment["Database__AllowInsecureConnection"] = "true";
        return Process.Start(info)!;
    }

    private static string ExtractTemporaryPassword(string stdout)
    {
        const string marker = "Temporary password:";
        var line = stdout.Split('\n').Single(candidate => candidate.Contains(marker));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
