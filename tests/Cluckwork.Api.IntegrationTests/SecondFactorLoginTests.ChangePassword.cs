using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1279: an enrolled user changes the password with a code (#1274 decision 11),
// guards G4.5 and G4.6, and G4.1 and G4.4 over /auth/change-password. An
// unenrolled user's change is unchanged apart from the missing-user answer.
public sealed partial class SecondFactorLoginTests
{
    private const string ChangePasswordPath = "/api/v1/auth/change-password";

    private sealed record PasswordSession(
        string Email, Guid AccountId, Guid UserId, EnrolledFactor? Factor, TokenPairDto Tokens, HttpClient Client);

    // What a change writes, read before any follow-up request rotates a row.
    private sealed record PasswordState(
        int Epoch, string? PasswordHash, int RefreshRows, int Revoked, int AccessFailed, int DailyFailures);

    private async Task<PasswordSession> PasswordSessionAsync(bool enrolled)
    {
        var email = $"cp-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var factor = enrolled ? await factory.EnrolTwoFactorAsync(email) : null;
        var tokens = await factory.LoginAsync(email);
        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken).Subject);
        return new(email, accountId, userId, factor, tokens, factory.CreateAuthedClient(tokens.AccessToken));
    }

    // "unenrolled" sends no code; "totp" and "recovery" are an enrolled user's two factors.
    private async Task<(PasswordSession Session, string? Code)> PasswordSessionAsync(string kind)
    {
        var session = await PasswordSessionAsync(enrolled: kind != "unenrolled");
        return (session, kind switch
        {
            "unenrolled" => null,
            "totp" => CodeAt(session.Factor!, 2),
            "recovery" => session.Factor!.RecoveryCodes[0],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    private Task<PasswordState> PasswordStateAsync(PasswordSession session) =>
        factory.WithTenantScopeAsync(session.AccountId, async db =>
        {
            var user = await db.Users.AsNoTracking().Where(u => u.Id == session.UserId)
                .Select(u => new { u.CredentialEpoch, u.PasswordHash, u.AccessFailedCount, u.SecondFactorFailures })
                .SingleAsync();
            var rows = db.RefreshTokens.Where(t => t.UserId == session.UserId);
            return new PasswordState(user.CredentialEpoch, user.PasswordHash, await rows.CountAsync(),
                await rows.CountAsync(t => t.RevokedAt != null), user.AccessFailedCount, user.SecondFactorFailures);
        });

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client, string currentPassword, string newPassword, string? code) =>
        client.PostWithKeyAsync(ChangePasswordPath, Guid.NewGuid().ToString(), code is null
            ? new { currentPassword, newPassword }
            : new { currentPassword, newPassword, code });

    // For an enrolled user a right password answers the code step's 401.
    private async Task<bool> PasswordSignsInAsync(string email, string password)
    {
        var response = await factory.TryLoginAsync(email, password);
        return response.StatusCode == HttpStatusCode.OK
            || (await TestHarness.ReadProblemAsync(response)).GetProperty("title").GetString() == SecondFactorErrorCodes.Required;
    }

    private async Task<HttpStatusCode> RefreshStatusAsync(PasswordSession session, string refreshToken) =>
        (await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(refreshToken, expectedAccount: session.AccountId.ToString())).StatusCode;

    private static string WeakPassword() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    [Theory]
    [InlineData("unenrolled")]
    [InlineData("totp")]
    [InlineData("recovery")]
    public async Task AnAcceptedChange_SwapsThePassword_AndEndsTheActingSession(string kind)
    {
        var (session, code) = await PasswordSessionAsync(kind);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await PasswordSignsInAsync(session.Email, NewPassword));
        Assert.False(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(session, session.Tokens.RefreshToken));
        if (kind == "recovery")
        {
            Assert.Equal(9, await RecoveryCodesLeftAsync(session.Factor!));
            var details = Assert.Single(await RecoveryAuditDetailsAsync(session.Factor!));
            Assert.Equal(9, JsonDocument.Parse(details!).RootElement.GetProperty("remaining").GetInt32());
        }
    }

    // Unchanged for an unenrolled user: a wrong current password charges
    // nothing and is not a failed login.
    [Fact]
    public async Task AWrongCurrentPassword_ForAnUnenrolledUser_ChargesNothing()
    {
        var (session, _) = await PasswordSessionAsync("unenrolled");
        factory.Sink.Events.Clear();

        var response = await ChangePasswordAsync(session.Client, WrongPassword, NewPassword, code: null);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(0, (await PasswordStateAsync(session)).AccessFailed);
        Assert.Empty(factory.EventsFor(SecurityEvents.LoginFailed));
    }

    // G4.6
    [Fact]
    public async Task AnEnrolledUser_WithoutACode_Is400SecondFactorRequired_AndNothingChanges()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        var before = await PasswordStateAsync(session);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code: null);

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Required), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(before, await PasswordStateAsync(session));
    }

    [Fact]
    public async Task AWrongCode_IsStepUpsBody_IsChargedOnce_AndChangesNothingElse()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        var before = await PasswordStateAsync(session);
        var atStepUp = await ProblemOf(await StepUpAsync(session.Client, TestHarness.Password, WrongCode(session.Factor!)));

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));

        Assert.Equal(atStepUp, await ProblemOf(response));
        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (atStepUp.Status, atStepUp.Title));
        var after = await PasswordStateAsync(session);
        Assert.Equal(before with { AccessFailed = 2, DailyFailures = 2 }, after);
        Assert.True(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.OK, await RefreshStatusAsync(session, session.Tokens.RefreshToken));
    }

    // The validators run before the code, so a weak password is the answer and
    // a wrong code sent with it is never checked or charged.
    [Fact]
    public async Task AWeakNewPassword_WithAWrongCode_IsRejectedAsWeak_AndChargesNothing()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        var before = await CountersAsync(session.Factor!);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, WeakPassword(), WrongCode(session.Factor!));

        Assert.Equal((HttpStatusCode.BadRequest, "Users.PasswordRejected"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(before, await CountersAsync(session.Factor!));
    }

    [Fact]
    public async Task AWeakNewPassword_WithAValidCode_SpendsNoCode()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        var before = await CountersAsync(session.Factor!);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, WeakPassword(), code);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.PasswordRejected"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(before, await CountersAsync(session.Factor!));
        Assert.Equal(HttpStatusCode.OK, (await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code)).StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPassword_ForAnEnrolledUser_IsChargedAndFiresOneLoginFailed()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        factory.Sink.Events.Clear();

        var response = await ChangePasswordAsync(session.Client, WrongPassword, NewPassword, code);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (response.StatusCode, (await ProblemOf(response)).Title));
        var failed = Assert.Single(factory.EventsFor(SecurityEvents.LoginFailed));
        Assert.False(failed.Properties.ContainsKey("UserId"));
        Assert.False(failed.Properties.ContainsKey("Email"));
        Assert.True(failed.Properties.ContainsKey("ClientIp"));
        Assert.Equal(1, (await CountersAsync(session.Factor!)).AccessFailed);
    }

    [Fact]
    public async Task ALockedEnrolledUser_IsAWrongCurrentPassword()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        for (var i = 0; i < 5; i++)
            await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));
        Assert.NotNull((await CountersAsync(session.Factor!)).LockoutEnd);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.False(await PasswordSignsInAsync(session.Email, NewPassword));
    }

    // The password was checked against one epoch, and the lock found another.
    [Fact]
    public async Task ASupersededAttempt_IsAWrongCurrentPassword_NeverA401()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == session.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + 1));
        });

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.False(await PasswordSignsInAsync(session.Email, NewPassword));
    }

    // An enrolled user's hash write runs under the user-row lock; an
    // unenrolled user's can still lose its check.
    [Fact]
    public async Task AnUnenrolledHashWrite_ThatLosesItsCheck_Is400UsersConflict()
    {
        var (session, _) = await PasswordSessionAsync("unenrolled");
        factory.BeforeCommand.Arm("UPDATE \"AspNetUsers\"", async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {session.UserId}""");
        });

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code: null);

        Assert.Equal((HttpStatusCode.BadRequest, "Users.Conflict"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.True(await PasswordSignsInAsync(session.Email, TestHarness.Password));
    }

    [Fact]
    public async Task ASpentDailyBudget_AtChangePassword_Is429WithRetryAfter()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        await SpendBudgetAsync(session.Factor!, factory.Clock.GetUtcNow());

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal((HttpStatusCode.TooManyRequests, new RetryLaterError(TimeSpan.Zero).Code),
            (response.StatusCode, (await ProblemOf(response)).Title));
        var retryAfter = TimeSpan.FromSeconds(double.Parse(response.Headers.GetValues("Retry-After").Single()));
        Assert.InRange(retryAfter, TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task AnUnreadableKey_RefusesAnAuthenticatorCode_AndARecoveryCodeStillWorks()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens
                .Where(t => t.UserId == session.UserId && t.LoginProvider == "Cluckwork" && t.Name == "AuthenticatorKey")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Value, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))));

        var refused = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);
        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.KeyUnavailable), (refused.StatusCode, (await ProblemOf(refused)).Title));

        var recovered = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, session.Factor!.RecoveryCodes[0]);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    // Over HTTP the epoch check answers 401 first.
    [Fact]
    public async Task AMissingUser_IsAWrongCurrentPassword()
    {
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        var result = await identity.ChangeOwnPasswordAsync(Guid.NewGuid(), Guid.NewGuid(), TestHarness.Password, NewPassword, code: null);

        Assert.Equal("Users.CurrentPasswordIncorrect", result.Error.Code);
    }

    // Decision 8: the pair proves both factors, so its lineage starts at the change.
    [Fact]
    public async Task AChangeWithACode_StartsTheEnrolledSessionCapAgain()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        factory.Clock.Advance(TimeSpan.FromDays(10));
        var changed = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, CodeAt(session.Factor!, 1));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        factory.Clock.Advance(TimeSpan.FromDays(29));
        var inside = await factory.CreateClient(TestHarness.Cookieless(factory)).PostRefreshAsync(
            TestHarness.ExtractRefreshCookie(changed, session.AccountId), expectedAccount: session.AccountId.ToString());
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        factory.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized,
            await RefreshStatusAsync(session, TestHarness.ExtractRefreshCookie(inside, session.AccountId)));
    }

    // G4.1 and G4.4 over change-password.
    [Fact]
    public async Task FiveWrongCodesAtChangePassword_LockTheAccount()
    {
        var (session, _) = await PasswordSessionAsync("totp");

        for (var i = 0; i < 4; i++)
            await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));
        Assert.Null((await CountersAsync(session.Factor!)).LockoutEnd);
        await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));

        Assert.NotNull((await CountersAsync(session.Factor!)).LockoutEnd);
    }

    // The login and change-password share one lockout, and a password-only
    // attempt clears nothing.
    [Fact]
    public async Task FourWrongCodesAtLogin_APasswordOnlyChange_AndOneWrongCode_LockTheAccount()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        var pending = await factory.PasswordStepAsync(session.Email);
        for (var i = 0; i < 4; i++) await PostCodeAsync(pending.Challenge, WrongCode(session.Factor!));

        var passwordOnly = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code: null);
        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Required), (passwordOnly.StatusCode, (await ProblemOf(passwordOnly)).Title));
        Assert.Equal(4, (await CountersAsync(session.Factor!)).AccessFailed);
        await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));

        Assert.NotNull((await CountersAsync(session.Factor!)).LockoutEnd);
    }

    // The enrolled twin of StepUp_LockoutTripsOnTheFifthUserAttempt_NotTheThird
    // at change-password: wrong passwords and a wrong code share one count,
    // and a right password resets nothing.
    [Fact]
    public async Task FourWrongPasswords_ThenTheRightPasswordWithAWrongCode_LockTheAccount()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        for (var i = 0; i < 4; i++)
            await ChangePasswordAsync(session.Client, WrongPassword, NewPassword, CodeAt(session.Factor!, 2));
        var afterFour = await CountersAsync(session.Factor!);
        Assert.Equal(4, afterFour.AccessFailed);
        Assert.Null(afterFour.LockoutEnd);

        await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));

        Assert.NotNull((await CountersAsync(session.Factor!)).LockoutEnd);
    }

    // G4.4 over change-password. One change lands; the other is refused as a
    // wrong current password. Three barriers stand behind it, in the order the
    // loser meets them: the epoch binding (the first change moves the epoch,
    // so VerifyAsync answers Superseded), the watermark, and the
    // current-password re-check under the lock. A loser whose password check
    // runs after the first change sees a wrong password, the same answer.
    [Fact]
    public async Task TwoConcurrentChanges_WithOneCode_ChangeThePasswordOnce()
    {
        var (session, code) = await PasswordSessionAsync("totp");

        var answers = await Task.WhenAll(
            ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code),
            ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(answers, answer => answer.StatusCode != HttpStatusCode.OK);
        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (refused.StatusCode, (await ProblemOf(refused)).Title));
        Assert.True(await PasswordSignsInAsync(session.Email, NewPassword));
    }

    // The failed-rehash twin at change-password: the reload keeps it working.
    [Fact]
    public async Task APasswordCheck_WhoseRehashSaveLost_StillChangesThePassword()
    {
        var (session, code) = await PasswordSessionAsync("totp");
        await ArmALostRehashSaveAsync(session.Factor!, rotateSecurityStamp: false);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.True(factory.BeforeCommand.Fired, "the concurrent write never landed before the rehash save");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The SPA sends an empty field as no code.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyCode_AtChangePassword_IsNoCode(string code)
    {
        var (session, _) = await PasswordSessionAsync("totp");

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Required), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(0, (await CountersAsync(session.Factor!)).AccessFailed);
    }

    [Fact]
    public async Task ACodeAcceptedAtLogin_IsRefusedAtChangePassword()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var code = CodeAt(factor, 1);
        var signedIn = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, code);
        var client = factory.CreateAuthedClient((await TestHarness.ReadTokensAsync(signedIn)).AccessToken);

        var reuse = await ChangePasswordAsync(client, TestHarness.Password, NewPassword, code);

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (reuse.StatusCode, (await ProblemOf(reuse)).Title));
        Assert.False(await PasswordSignsInAsync(email, NewPassword));
    }

    // G4.5: a fault before the commit rolls everything back, and the same code
    // is accepted again.
    public static TheoryData<string, string, HttpStatusCode> FaultsBeforeTheCommit => new()
    {
        { "unenrolled", "INSERT INTO refresh_tokens", HttpStatusCode.Conflict },
        { "unenrolled", "UPDATE refresh_tokens", HttpStatusCode.InternalServerError },
        { "totp", "INSERT INTO refresh_tokens", HttpStatusCode.Conflict },
        { "totp", "UPDATE refresh_tokens", HttpStatusCode.InternalServerError },
        { "recovery", "INSERT INTO refresh_tokens", HttpStatusCode.Conflict },
        { "recovery", "UPDATE refresh_tokens", HttpStatusCode.InternalServerError },
    };

    [Theory]
    [MemberData(nameof(FaultsBeforeTheCommit))]
    public async Task AFaultBeforeTheCommit_RollsTheChangeBack(string kind, string command, HttpStatusCode expected)
    {
        var (session, code) = await PasswordSessionAsync(kind);
        var before = await PasswordStateAsync(session);
        factory.CommandFault.Arm(command, afterExecution: false);

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await PasswordStateAsync(session));
        Assert.True(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.OK, (await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code)).StatusCode);
    }

    // The commit lands and its acknowledgement is lost: today's 500, no pair.
    [Theory]
    [InlineData("unenrolled")]
    [InlineData("totp")]
    [InlineData("recovery")]
    public async Task ACommitFailure_AtChangePassword_Is500WithNoPair(string kind)
    {
        var (session, code) = await PasswordSessionAsync(kind);
        factory.CommitFault.ArmOnce();

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, code);
        // Read before any follow-up request commits or signs in.
        var commits = factory.CommitFault.Commits;
        int? codesLeft = kind == "recovery" ? await RecoveryCodesLeftAsync(session.Factor!) : null;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, commits);
        Assert.False(response.Headers.Contains("Set-Cookie"), "a failed commit handed out a refresh cookie");
        Assert.False((await TestHarness.ReadProblemAsync(response)).TryGetProperty("accessToken", out _),
            "a failed commit handed out an access token");
        if (kind == "recovery")
            Assert.Equal(9, codesLeft);
        Assert.True(await PasswordSignsInAsync(session.Email, NewPassword));
        Assert.False(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(session, session.Tokens.RefreshToken));
    }

    // A refused outcome's charge commits even when the acknowledgement is lost.
    [Fact]
    public async Task AWrongCode_WhoseCommitAcknowledgementIsLost_StaysCharged_AndChangesNothingElse()
    {
        var (session, _) = await PasswordSessionAsync("totp");
        var before = await PasswordStateAsync(session);
        Assert.Equal((0, 0), (before.AccessFailed, before.DailyFailures));
        factory.CommitFault.ArmOnce();

        var response = await ChangePasswordAsync(session.Client, TestHarness.Password, NewPassword, WrongCode(session.Factor!));
        var commits = factory.CommitFault.Commits;
        var after = await PasswordStateAsync(session);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, commits);
        Assert.Equal(before with { AccessFailed = 1, DailyFailures = 1 }, after);
        Assert.True(await PasswordSignsInAsync(session.Email, TestHarness.Password));
        Assert.Equal(HttpStatusCode.OK, await RefreshStatusAsync(session, session.Tokens.RefreshToken));
    }
}
