using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1284 G7.11: the clear-passkeys operator verb (record 1282 decision 10), run
// as the real process. Its own factory, so --all sees only these farms.
public sealed class ClearPasskeysCommandTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    private static readonly string ApiDllPath = typeof(Program).Assembly.Location;
    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(60);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{ApiDllPath}\" clear-passkeys {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["ConnectionStrings__Default"] = factory.ConnectionString;
        psi.Environment["Database__Provider"] = "Postgres";
        psi.Environment["Database__AllowInsecureConnection"] = "true";
        psi.Environment["Jwt__Issuer"] = "cluckwork-test";
        psi.Environment["Jwt__Audience"] = "cluckwork-api-test";
        psi.Environment["Jwt__PublicKeyPem"] = TestJwtKeys.PublicKeyPem;
        psi.Environment["Jwt__PrivateKeyPem"] = TestJwtKeys.PrivateKeyPem;
        return SeedCommandRunner.RunToCompletionAsync(Process.Start(psi)!, SubprocessTimeout);
    }

    private sealed record Member(Guid AccountId, Guid UserId, TokenPairDto Session);

    // A farm whose Owner has two passkeys and whose second user has one, both
    // signed in, and a third user with none.
    private async Task<(string Slug, Member[] WithPasskeys, Member Without)> FarmAsync()
    {
        var ownerEmail = $"clear-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var otherEmail = $"clear-{Guid.NewGuid():N}@test.local";
        var plainEmail = $"clear-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, otherEmail, Cluckwork.Domain.Modules.Farm.Contracts.Roles.Manager);
        await factory.SeedUserAsync(accountId, plainEmail, Cluckwork.Domain.Modules.Farm.Contracts.Roles.Manager);
        async Task<Member> MemberAsync(string email, int passkeys)
        {
            var userId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
            // Signed in first: a user with a passkey gets the second step for a password (#1285).
            var session = await factory.LoginAsync(email);
            if (passkeys > 0) await factory.SeedPasskeysAsync(accountId, userId, passkeys);
            return new Member(accountId, userId, session);
        }
        var slug = "farm-" + accountId.ToString("N")[..12];
        return (slug, [await MemberAsync(ownerEmail, 2), await MemberAsync(otherEmail, 1)], await MemberAsync(plainEmail, 0));
    }

    private async Task<HttpStatusCode> MeAsync(Member member) =>
        (await factory.CreateAuthedClient(member.Session.AccessToken).GetAsync("/api/v1/me")).StatusCode;

    private Task<List<(Guid EntityId, Guid AccountId, string Actor, string? Reason, string? Details)>> ClearedRowsAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, async db => (await db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "User.PasskeysCleared")
            .Select(a => new { a.EntityId, a.AccountId, a.ActorEmail, a.Reason, a.DetailsJson }).ToListAsync())
            .Select(a => (a.EntityId, a.AccountId, a.ActorEmail, a.Reason, a.DetailsJson)).ToList());

    private async Task AssertClearedAsync(Member member, int had)
    {
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(member.AccountId, member.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(member));
        var row = Assert.Single(await ClearedRowsAsync(member.AccountId), r => r.EntityId == member.UserId);
        Assert.Equal((member.AccountId, "(clear-passkeys)", "moving the site"), (row.AccountId, row.Actor, row.Reason));
        Assert.Equal(had, JsonDocument.Parse(row.Details!).RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task AFarmScope_ClearsOnlyThatFarm_EndsItsSessions_AndWritesOneRowPerUser()
    {
        var (slug, cleared, untouched) = await FarmAsync();
        var (_, otherFarm, _) = await FarmAsync();

        var (exitCode, stdout, stderr) = await RunAsync($"--farm {slug.ToUpperInvariant()} --reason \"moving the site\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        await AssertClearedAsync(cleared[0], had: 2);
        await AssertClearedAsync(cleared[1], had: 1);
        Assert.Equal(2, (await ClearedRowsAsync(cleared[0].AccountId)).Count);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(untouched));
        Assert.Equal((2, 2), await factory.PasskeyStateAsync(otherFarm[0].AccountId, otherFarm[0].UserId));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(otherFarm[0]));
        Assert.Empty(await ClearedRowsAsync(otherFarm[0].AccountId));
    }

    [Fact]
    public async Task AllFarms_ClearsEveryFarm_UnderEachFarmsOwnTenant()
    {
        var (_, first, _) = await FarmAsync();
        var (_, second, _) = await FarmAsync();

        var (exitCode, stdout, stderr) = await RunAsync("--all --reason \"moving the site\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        foreach (var member in first.Concat(second))
            await AssertClearedAsync(member, had: member == first[0] || member == second[0] ? 2 : 1);
    }

    [Theory]
    [InlineData("--reason \"no scope\"")]
    [InlineData("--all --farm some-farm")]
    [InlineData("--reason drill --all --farm")]
    [InlineData("--all --farm \"\"")]
    [InlineData("--farm \"\"")]
    [InlineData("--all --farm=some-farm")]
    [InlineData("--all --Farm some-farm")]
    [InlineData("--reason --all")]
    [InlineData("--farm --all")]
    [InlineData("--farm no-such-farm")]
    // A flag with no value: each red when the missing-value check goes.
    [InlineData("--all --reason --farm")]
    [InlineData("--farm {farm} --reason --all")]
    // A repeated flag.
    [InlineData("--farm {farm} --farm {farm}")]
    [InlineData("--farm {farm} --reason one --reason two")]
    [InlineData("--all --all")]
    // {farm} is the seeded farm's real code.
    public async Task AMalformedScope_ExitsNonZero_AndChangesNothing(string arguments)
    {
        var (slug, members, _) = await FarmAsync();

        var (exitCode, stdout, stderr) = await RunAsync(arguments.Replace("{farm}", slug, StringComparison.Ordinal));

        Assert.True(exitCode != 0, $"expected a refusal, got exit 0. stdout={stdout} stderr={stderr}");
        Assert.Equal((2, 2), await factory.PasskeyStateAsync(members[0].AccountId, members[0].UserId));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(members[0]));
        Assert.Empty(await ClearedRowsAsync(members[0].AccountId));
    }
}
