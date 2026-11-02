using System.Net;
using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1274 decisions 3, 4 and 14: what changes between the two steps, replays,
// and the commit the code step cannot see land.
public sealed partial class SecondFactorLoginTests
{
    private static readonly string NewPassword = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    // G3.3
    [Fact]
    public async Task OneChallengeAndOneCode_ThroughTwoInstances_GiveOneSession()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        var code = CodeAt(factor, 1);
        using var secondInstance = factory.WithWebHostBuilder(_ => { });
        var body = new { challenge = pending.Challenge, code };

        var answers = await Task.WhenAll(
            factory.CreateClient().PostAsJsonAsync(TestHarness.SecondFactorPath, body),
            secondInstance.CreateClient().PostAsJsonAsync(TestHarness.SecondFactorPath, body));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await RefreshRowsAsync(factor));
    }

    [Fact]
    public async Task ACodeAcceptedAtLogin_OrAtConfirm_IsRefusedWithALaterChallenge()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var atConfirm = CodeAt(factor, 0);
        Assert.Equal(SecondFactorErrorCodes.Incorrect,
            (await ProblemOf(await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, atConfirm))).Title);

        var code = CodeAt(factor, 1);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, code)).StatusCode);
        var reuse = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, code);

        Assert.Equal(SecondFactorErrorCodes.Incorrect, (await ProblemOf(reuse)).Title);
    }

    // G3.4: each binding on its own, with no other column moved.
    [Fact]
    public async Task AnEpochOnlyChange_BetweenTheSteps_EndsTheChallenge()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        await SupersedeAsync(email);

        await AssertEndedAsync(factor, pending, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AStampOnlyChange_BetweenTheSteps_EndsTheChallenge()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(factor.UserId.ToString()))!)).Succeeded);
        }

        await AssertEndedAsync(factor, pending, HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> RealEvents =>
        ["password change", "Owner password reset", "disable", "disable then enable", "suspension", "authenticator replacement"];

    [Fact]
    public void TheRealEventsTheory_HasItsFloor() => Assert.True(RealEvents.Count >= 6);

    [Theory]
    [MemberData(nameof(RealEvents))]
    public async Task ARealEventBetweenTheSteps_EndsTheChallenge(string change)
    {
        var ownerEmail = $"tf-owner-{Guid.NewGuid():N}@test.local";
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var ownerId = await factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == ownerEmail).Select(u => u.Id).SingleAsync());
        var pending = await factory.PasswordStepAsync(email);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ResolveTenantAndActor(accountId, ownerId, ownerEmail);
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            var done = change switch
            {
                "password change" => (await identity.ChangeOwnPasswordAsync(factor.UserId, TestHarness.Password, NewPassword)).IsSuccess,
                "Owner password reset" => (await identity.SetUserPasswordAsync(accountId, factor.UserId, NewPassword)).IsSuccess,
                "disable" => (await identity.DisableUserAsync(accountId, factor.UserId, ownerId, "test")).IsSuccess,
                "disable then enable" => (await identity.DisableUserAsync(accountId, factor.UserId, ownerId, "test")).IsSuccess
                    && (await identity.EnableUserAsync(accountId, factor.UserId, ownerId)).IsSuccess,
                "suspension" => (await scope.ServiceProvider.GetRequiredService<AccountSuspensionService>().SuspendAsync(accountId, "test")).IsSuccess,
                "authenticator replacement" => await ReplaceAuthenticatorAsync(scope, factor),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            Assert.True(done, change);
        }

        await AssertEndedAsync(factor, pending, HttpStatusCode.Unauthorized);
    }

    private static async Task<bool> ReplaceAuthenticatorAsync(IServiceScope scope, EnrolledFactor factor)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credentials = scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var start = await TestHarness.InTransactionAsync(db, ct => credentials.StartEnrolmentAsync(factor.AccountId, factor.UserId, ct));
        var code = TotpMatcher.Compute(TotpMatcher.DecodeBase32(start.Key), TotpMatcher.StepAt(clock.GetUtcNow()) + 1);
        return await TestHarness.InTransactionAsync(db, ct =>
            credentials.ConfirmEnrolmentAsync(factor.AccountId, factor.UserId, new SecondFactorCode.Totp(code), ct)) is ConfirmOutcome.Replaced;
    }

    private async Task AssertEndedAsync(EnrolledFactor factor, PendingLogin pending, HttpStatusCode expected)
    {
        var before = await CountersAsync(factor);
        var rows = await RefreshRowsAsync(factor);

        var response = await PostCodeAsync(pending.Challenge, CodeAt(factor, 2));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(rows, await RefreshRowsAsync(factor));
        var after = await CountersAsync(factor);
        Assert.Equal((before.AccessFailed, before.DailyFailures, before.SignIns), (after.AccessFailed, after.DailyFailures, after.SignIns));
    }

    // G3.14: Postgres commits and the acknowledgement is lost; the minted
    // refresh row proves the unit committed.
    [Fact]
    public async Task ALostCommitAcknowledgement_StillDeliversTheSession()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        var code = CodeAt(factor, 1);

        factory.CommitFault.ArmOnce();
        var response = await PostCodeAsync(pending.Challenge, code);

        Assert.True(factory.CommitFault.Commits >= 1, "the code step committed nothing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await TestHarness.ReadTokensAsync(response);
        var refresh = await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: factor.AccountId.ToString());
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // G3.16: a reset that commits after the code step's mint supersedes what it
    // minted. Sequential: the lock is what keeps a concurrent reset to this
    // order, and the record names no mutation that isolates it.
    [Fact]
    public async Task APasswordResetRightAfterTheCodeStep_RefusesTheNewSession()
    {
        var ownerEmail = $"tf-owner-{Guid.NewGuid():N}@test.local";
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var factor = await factory.EnrolTwoFactorAsync(email);
        var tokens = await factory.LoginAsync(email);

        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId))
            Assert.True((await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
                .SetUserPasswordAsync(accountId, factor.UserId, NewPassword)).IsSuccess);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(tokens.AccessToken)).StatusCode);
        var refresh = await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    // G3.19, the shape of RecordLogout_FencesAStaleFullEntityWrite.
    [Fact]
    public async Task AStaleFullEntityWrite_CannotLowerTheWatermarkOrTheSignInCount()
    {
        var (email, factor) = await SeedEnrolledAsync();
        using var staleScope = factory.Services.CreateScope();
        var staleUsers = staleScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var stale = (await staleUsers.FindByIdAsync(factor.UserId.ToString()))!;

        await factory.LoginAsync(email);
        var signedIn = await CountersAsync(factor);
        stale.DisplayName = "Stale write";
        var write = await staleUsers.UpdateAsync(stale);

        Assert.False(write.Succeeded, "the stale write was not fenced");
        var after = await CountersAsync(factor);
        Assert.Equal((signedIn.Watermark, signedIn.SignIns), (after.Watermark, after.SignIns));
    }

    // Issue criterion "MustChangePassword (#283)", decision 7: the code step
    // comes first, and the forced change then gates the session it minted.
    [Fact]
    public async Task AnEnrolledUserWhoMustChangeTheirPassword_GetsTheCodeStepFirst_ThenTheForcedChange()
    {
        var (email, factor) = await SeedEnrolledAsync();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == factor.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, true));

        var pending = await factory.PasswordStepAsync(email);
        Assert.Equal(0, await RefreshRowsAsync(factor));
        var signedIn = await PostCodeAsync(pending.Challenge, CodeAt(factor, 1));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var tokens = await TestHarness.ReadTokensAsync(signedIn);
        Assert.Equal("true", new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken)
            .Claims.Single(c => c.Type == "must_change_password").Value);

        var gated = await GetMeAsync(tokens.AccessToken);
        Assert.Equal((HttpStatusCode.Forbidden, "Auth.MustChangePassword"), (gated.StatusCode, (await ProblemOf(gated)).Title));

        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }
}
