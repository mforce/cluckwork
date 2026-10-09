using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using System.Net;
using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

public sealed class CredentialEpochFloorFactory : CluckworkWebApplicationFactory
{
    public CountingPasswordHasher Hasher { get; } = new();
    public SecurityEventLoggingFactory.CollectingSink Sink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(Hasher);
            services.AddSingleton<ILogEventSink>(Sink);
        });
    }
}

// #1031 — a stored CredentialEpoch below 1 must never admit a credential. The
// CHECK constraint makes such a row unwritable, so these tests drop it on this
// factory's own database to prove that login, the verifier and refresh each
// refuse it without the constraint's help.
public sealed class CredentialEpochFloorTests(CredentialEpochFloorFactory factory)
    : IClassFixture<CredentialEpochFloorFactory>
{
    private sealed record TokenRow(
        string TokenHash, int IssuedEpoch, DateTimeOffset? RevokedAt,
        string? ReplacedByTokenHash, bool RevokedByGrace, string ConcurrencyStamp);

    // Nonzero and set, so a refusal that resets or clears lockout state shows up.
    // The lockout end is in the past: a live lockout would refuse the login on
    // its own and hide a missing epoch refusal.
    private const int SeededFailedAccessCount = 2;
    private static readonly DateTimeOffset SeededLockoutEnd = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-epoch")]
    [InlineData("0")]
    public async Task StoredEpochZero_RefusesClaimlessMalformedAndZeroBearers(string? claim)
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(0);

        var response = await factory.CreateAuthedClient(
            CredentialEpochTests.CreateAccessToken(userId, accountId, claim)).GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", await TitleAsync(response));
    }

    [Theory]
    [InlineData("disabled", "Auth.AccountDisabled")]
    [InlineData("suspended", "Auth.FarmSuspended")]
    public async Task StoredEpochZero_KeepsDisabledAndSuspendedPrecedence(string state, string expectedTitle)
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(0);
        await factory.WithTenantScopeAsync(accountId, db => state == "disabled"
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "DisabledAt" = now() WHERE "Id" = {userId}""")
            : db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Accounts" SET "IsActive" = FALSE WHERE "Id" = {accountId}"""));

        var response = await factory.CreateAuthedClient(
            CredentialEpochTests.CreateAccessToken(userId, accountId, "0")).GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(expectedTitle, await TitleAsync(response));
    }

    // HTTP parsing already rejects a signed negative claim, so only a direct
    // call can show the verifier refusing a matching negative epoch.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Verifier_RefusesAMatchingEpochBelowOne(int epoch)
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(epoch);

        var verdict = await factory.WithTenantScopeAsync(accountId,
            db => new CredentialEpochVerifier(db).VerifyAsync(userId, accountId, epoch, connectedApp: false));

        Assert.Equal(CredentialVerdict.Superseded, verdict);
    }

    [Theory]
    [InlineData(0, TestHarness.Password)]
    [InlineData(0, "wrong-password")]
    [InlineData(-1, TestHarness.Password)]
    [InlineData(-1, "wrong-password")]
    public async Task Login_RefusesAStoredEpochBelowOne_LikeADisabledUser(int storedEpoch, string password)
    {
        var (accountId, userId, email) = await SeedWithStoredEpochAsync(storedEpoch);
        var loginFailed = CountEvents(SecurityEvents.LoginFailed);
        factory.Hasher.Reset();

        var response = await factory.TryLoginAsync(email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Identity.InvalidCredentials", await TitleAsync(response));
        Assert.False(response.Headers.Contains("Set-Cookie"), "a refused login must not set a refresh cookie");
        Assert.Empty(await TokenRowsAsync(accountId, userId));
        Assert.Equal(1, factory.Hasher.VerifyCount);
        Assert.Equal(loginFailed + 1, CountEvents(SecurityEvents.LoginFailed));
        await AssertLockoutUntouchedAsync(accountId, userId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Refresh_RefusesAMatchingIssuedEpochBelowOne_WithoutRotating(int epoch)
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(epoch);
        var presented = await AddRefreshTokenAsync(accountId, userId, epoch);
        var before = await TokenRowsAsync(accountId, userId);

        var response = await factory.CreateClient().PostRefreshAsync(
            presented, expectedAccount: accountId.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Identity.InvalidRefreshToken", await TitleAsync(response));
        Assert.Equal(before, await TokenRowsAsync(accountId, userId));
    }

    // Without the floor ahead of it, the replay branch reads this revoked row as
    // theft and revokes the live sibling, still answering 401.
    [Fact]
    public async Task RevokedEpochZeroRefresh_IsRefusedBeforeReplayDetection()
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(0);
        var presented = await AddRefreshTokenAsync(
            accountId, userId, 0, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await AddRefreshTokenAsync(accountId, userId, 0);
        var before = await TokenRowsAsync(accountId, userId);
        var replays = CountEvents(SecurityEvents.RefreshTokenReplayDetected);

        var response = await factory.CreateClient().PostRefreshAsync(
            presented, expectedAccount: accountId.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await TokenRowsAsync(accountId, userId));
        Assert.Contains(before, row => row.RevokedAt is null);
        Assert.Equal(replays, CountEvents(SecurityEvents.RefreshTokenReplayDetected));
        await AssertLockoutUntouchedAsync(accountId, userId);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cleared = Assert.Single(cookies!, cookie => cookie.StartsWith(
            AuthCookies.RefreshCookieNameFor(accountId) + "=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoredEpochZero_StopsStepUpBeforeAnyPasswordCheck()
    {
        var (accountId, userId, _) = await SeedWithStoredEpochAsync(0);
        factory.Hasher.Reset();

        var response = await factory.CreateAuthedClient(
                CredentialEpochTests.CreateAccessToken(userId, accountId, "0"))
            .PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", await TitleAsync(response));
        Assert.Equal(0, factory.Hasher.VerifyCount);
    }

    private async Task<(Guid AccountId, Guid UserId, string Email)> SeedWithStoredEpochAsync(int storedEpoch)
    {
        var email = $"epoch-floor-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "AspNetUsers" DROP CONSTRAINT IF EXISTS "CK_AspNetUsers_CredentialEpoch" """);
            var id = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AspNetUsers"
                SET "CredentialEpoch" = {storedEpoch},
                    "AccessFailedCount" = {SeededFailedAccessCount},
                    "LockoutEnd" = {SeededLockoutEnd}
                WHERE "Id" = {id}
                """);
            return id;
        });
        return (accountId, userId, email);
    }

    private async Task<string> AddRefreshTokenAsync(
        Guid accountId, Guid userId, int issuedEpoch, DateTimeOffset? revokedAt = null)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                TokenHash = CredentialEpochTests.HashToken(raw),
                IssuedEpoch = issuedEpoch,
                CreatedAt = now,
                ExpiresAt = now.AddDays(1),
                RevokedAt = revokedAt,
            });
            await db.SaveChangesAsync();
        });
        return raw;
    }

    private Task<List<TokenRow>> TokenRowsAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.TokenHash)
            .Select(token => new TokenRow(token.TokenHash, token.IssuedEpoch, token.RevokedAt,
                token.ReplacedByTokenHash, token.RevokedByGrace, token.ConcurrencyStamp))
            .ToListAsync());

    private async Task AssertLockoutUntouchedAsync(Guid accountId, Guid userId)
    {
        var state = await factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(user => user.Id == userId)
            .Select(user => new { user.AccessFailedCount, user.LockoutEnd })
            .SingleAsync());
        Assert.Equal(SeededFailedAccessCount, state.AccessFailedCount);
        Assert.Equal(SeededLockoutEnd, state.LockoutEnd);
    }

    private int CountEvents(string securityEvent) => factory.Sink.Events.Count(e =>
        e.Properties.TryGetValue("SecurityEvent", out var value)
        && value is ScalarValue { Value: string name } && name == securityEvent);

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())?.Title;
}

[Collection(IntegrationCollection.Name)]
public sealed class CredentialEpochFloorConstraintTests(CluckworkWebApplicationFactory factory)
{
    private const string PreviousMigration = "20260924232529_AddEggGradeLowStockFloor";

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Database_RefusesAStoredEpochBelowOne(int epoch)
    {
        var email = $"epoch-floor-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var error = await Assert.ThrowsAsync<PostgresException>(() => factory.WithTenantScopeAsync(accountId,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "CredentialEpoch" = {epoch} WHERE "Email" = {email}""")));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("CK_AspNetUsers_CredentialEpoch", error.ConstraintName);
    }

    [Fact]
    public async Task Migration_RefusesAPreExistingBadRow_AndLeavesItUnchanged()
    {
        await using var postgres = new SharedPostgresDatabase();
        await postgres.StartAsync();
        await using var db = BusinessRecordChronologyMigrationTests.BuildContext(postgres.GetConnectionString());
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var userId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
                ("Id", "AccountId", "MustChangePassword", "UserName", "NormalizedUserName",
                 "Email", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed",
                 "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CredentialEpoch",
                 "StepUpLogoutEpoch")
            VALUES ({userId}, {SeedDefaults.AccountId}, FALSE, 'epoch-zero@test.local',
                    'EPOCH-ZERO@TEST.LOCAL', 'epoch-zero@test.local',
                    'EPOCH-ZERO@TEST.LOCAL', FALSE, FALSE, FALSE, FALSE, 0, 0, 0)
            """);

        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("CK_AspNetUsers_CredentialEpoch", error.ConstraintName);
        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"""SELECT "CredentialEpoch" AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId}""").SingleAsync());
        Assert.Equal(PreviousMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }
}
