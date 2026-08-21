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
    [InlineData("--timezone America/Los_Angeles")]
    [InlineData("--time-zone America/Los_Angeles")]
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
