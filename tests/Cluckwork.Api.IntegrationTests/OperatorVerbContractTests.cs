using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #857 — two things the operator verbs forward that no suite pinned exactly.
// provision-account's success line was checked with Contains(email), which a
// result that swapped the farm code and the owner email still satisfies, and
// no CLI test passed recover-admin's --account.
[Collection(IntegrationCollection.Name)]
public sealed class OperatorVerbContractTests(CluckworkWebApplicationFactory factory)
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string arguments)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__Default"] = factory.ConnectionString;
        info.Environment["Database__Provider"] = "Postgres";
        info.Environment["Database__AllowInsecureConnection"] = "true";
        return SeedCommandRunner.RunToCompletionAsync(
            Process.Start(info)!, SubprocessTimeout, $"`{arguments}` did not exit");
    }

    private async Task<string> SlugAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync();
    }

    private Task<HttpResponseMessage> LoginAsync(string farmCode, string email, string password) =>
        factory.CreateClient(TestHarness.Cookieless(factory))
            .PostAsJsonAsync("/api/v1/auth/login", new { farmCode, email, password });

    private static string TemporaryPassword(string stdout)
    {
        const string marker = "Temporary password:";
        var line = stdout.Split('\n').Single(candidate => candidate.Contains(marker));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    [Fact]
    public async Task ProvisionAccount_PrintsTheCommittedFarmCodeAccountAndOwner()
    {
        _ = factory.Services;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var slug = $"line-{suffix}";
        var email = $"line-owner-{suffix}@example.test";

        var (exitCode, stdout, stderr) = await RunAsync(
            $"provision-account --name \"Line Farm\" --slug {slug} --owner-email {email}");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        using var scope = factory.Services.CreateScope();
        var accountId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts
            .IgnoreQueryFilters().Where(a => a.Slug == slug).Select(a => a.Id).SingleAsync();
        Assert.Contains($"Farm provisioned: {slug} (account {accountId}); Owner {email}.", stdout);
    }

    // One email is an Owner in two farms. Without --account the verb refuses
    // as ambiguous; with it, only the named farm's user is reset and audited.
    [Fact]
    public async Task RecoverAdmin_WithAccount_ResetsOnlyThatFarmsUser_AndAuditsTheReason()
    {
        var email = $"two-farm-{Guid.NewGuid():N}@test.local";
        var farmA = await factory.SeedAccountWithUserAsync(email);
        var farmB = await factory.SeedAccountWithUserAsync($"b-owner-{Guid.NewGuid():N}@test.local");
        await factory.SeedUserAsync(farmB, email, Roles.Owner);
        var slugA = await SlugAsync(farmA);
        var slugB = await SlugAsync(farmB);

        var (exitCode, stdout, stderr) = await RunAsync(
            $"recover-admin --email {email} --account {farmB} --reason \"two-farm drill\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains($"on farm {slugB} (account {farmB})", stdout);
        var password = TemporaryPassword(stdout);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(slugB, email, password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(slugB, email, TestHarness.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(slugA, email, TestHarness.Password)).StatusCode);

        var audit = await factory.WithTenantScopeAsync(farmB, db => db.AuditEvents
            .Where(a => a.AccountId == farmB && a.Action == AuditActions.UserBreakGlassReset)
            .SingleAsync());
        Assert.Equal("two-farm drill", audit.Reason);
        Assert.False(await factory.WithTenantScopeAsync(farmA, db => db.AuditEvents
            .AnyAsync(a => a.AccountId == farmA && a.Action == AuditActions.UserBreakGlassReset)));
    }
}
