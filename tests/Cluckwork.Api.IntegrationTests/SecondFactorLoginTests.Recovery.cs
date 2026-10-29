using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1274 decisions 2 and 9: recovery codes, single-use challenges, and what an
// enrolment does to sessions minted before it.
public sealed partial class SecondFactorLoginTests
{
    private Task<int> RecoveryCodesLeftAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.UserTokens.CountAsync(t =>
            t.UserId == factor.UserId && t.LoginProvider == "Cluckwork" && t.Name.StartsWith("RecoveryCode:")));

    private Task<List<string?>> RecoveryAuditDetailsAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityId == factor.UserId && e.Action == AuditActions.UserRecoveryCodeUsed)
            .Select(e => e.DetailsJson).ToListAsync());

    private async Task<ProofBinding> StepUpBindingAsync(EnrolledFactor factor)
    {
        var user = await factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.AsNoTracking()
            .SingleAsync(u => u.Id == factor.UserId));
        return new ProofBinding(user.CredentialEpoch, ProofBinding.HashStamp(user.SecurityStamp), SignIns: null);
    }

    private async Task<FactorOutcome> VerifyInOwnScopeAsync(EnrolledFactor factor, SecondFactorCode code, ProofBinding binding)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var credentials = scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>();
        return await TestHarness.InTransactionAsync(db, ct => credentials.VerifyAsync(factor.AccountId, factor.UserId, code, binding, ct));
    }

    private static SecondFactorCode Parsed(string raw) =>
        SecondFactorCode.TryParse(raw, out var code) ? code : throw new ArgumentException(raw);

    // G3.11
    [Fact]
    public async Task ARecoveryCode_WorksOnce_InAnyCase_AndIsAudited()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var typed = factor.RecoveryCodes[0].Replace("-", "").ToLowerInvariant();

        var first = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, typed);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(9, await RecoveryCodesLeftAsync(factor));
        var details = Assert.Single(await RecoveryAuditDetailsAsync(factor));
        Assert.Equal(9, JsonDocument.Parse(details!).RootElement.GetProperty("remaining").GetInt32());

        // A challenge issued after the first use, so the sign-in count cannot
        // be what refuses it.
        var again = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, factor.RecoveryCodes[0]);
        Assert.Equal(SecondFactorErrorCodes.Incorrect, (await ProblemOf(again)).Title);
        Assert.Equal(9, await RecoveryCodesLeftAsync(factor));
    }

    [Fact]
    public async Task ConcurrentRedemptions_SpendEachCodeOnce()
    {
        var (_, factor) = await SeedEnrolledAsync();
        var binding = await StepUpBindingAsync(factor);

        var different = await Task.WhenAll(
            VerifyInOwnScopeAsync(factor, Parsed(factor.RecoveryCodes[1]), binding),
            VerifyInOwnScopeAsync(factor, Parsed(factor.RecoveryCodes[2]), binding));
        Assert.All(different, outcome => Assert.IsType<FactorOutcome.Accepted>(outcome));

        var same = await Task.WhenAll(
            VerifyInOwnScopeAsync(factor, Parsed(factor.RecoveryCodes[3]), binding),
            VerifyInOwnScopeAsync(factor, Parsed(factor.RecoveryCodes[3]), binding));
        Assert.Single(same, outcome => outcome is FactorOutcome.Accepted);
        Assert.Equal(7, await RecoveryCodesLeftAsync(factor));
    }

    [Fact]
    public async Task ASupersededAttempt_LeavesTheTenantUnresolved_AndWritesNoAuditRow()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        await SupersedeAsync(email);

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
            .CompleteSecondFactorLoginAsync(pending.Challenge, factor.RecoveryCodes[0]);

        Assert.Equal(SecondFactorErrorCodes.ChallengeExpired, result.Error.Code);
        Assert.False(scope.ServiceProvider.GetRequiredService<TenantContext>().IsResolved);
        Assert.Empty(await RecoveryAuditDetailsAsync(factor));
        Assert.Equal(10, await RecoveryCodesLeftAsync(factor));
    }

    // G3.13: a value the ring cannot read, as after a certificate change.
    [Fact]
    public async Task AnUnreadableKey_RefusesAuthenticatorCodesUncharged_AndRecoveryCodesStillWork()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var foreign = new EphemeralDataProtectionProvider().CreateProtector("another-ring").Protect(factor.Key);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().UserTokens
                .Where(t => t.UserId == factor.UserId && t.LoginProvider == "Cluckwork" && t.Name == "AuthenticatorKey")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Value, foreign));
        factory.Sink.Events.Clear();
        var pending = await factory.PasswordStepAsync(email);

        var totp = await PostCodeAsync(pending.Challenge, CodeAt(factor, 1));
        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.KeyUnavailable), (totp.StatusCode, (await ProblemOf(totp)).Title));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
        Assert.Single(factory.EventsFor(SecurityEvents.SecondFactorKeyUnreadable));

        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, factor.RecoveryCodes[0])).StatusCode);
    }

    // G3.27
    [Fact]
    public async Task Enrolment_EndsTheSessionsAndConnectedAppsMintedBeforeIt()
    {
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var before = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, before.AccessToken);

        await factory.EnrolTwoFactorAsync(email);

        var refresh = await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(before.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var probe = await ConnectedAppToken.Client(factory, app).GetAsync(SecondFactorFactory.OAuthProbePath);
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
    }

    // G3.30
    [Fact]
    public async Task AChallenge_CompletesOneSignIn_ByEitherFactor()
    {
        var (email, factor) = await SeedEnrolledAsync();

        var totp = (await factory.PasswordStepAsync(email)).Challenge;
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(totp, CodeAt(factor, 1))).StatusCode);
        await AssertSpentAsync(factor, totp, CodeAt(factor, 2));

        var recovery = (await factory.PasswordStepAsync(email)).Challenge;
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(recovery, factor.RecoveryCodes[0])).StatusCode);
        await AssertSpentAsync(factor, recovery, factor.RecoveryCodes[1]);
    }

    private async Task AssertSpentAsync(EnrolledFactor factor, string challenge, string freshCode)
    {
        var before = await CountersAsync(factor);
        var rows = await RefreshRowsAsync(factor);

        var replay = await PostCodeAsync(challenge, freshCode);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), (replay.StatusCode, (await ProblemOf(replay)).Title));
        Assert.Equal(rows, await RefreshRowsAsync(factor));
        var after = await CountersAsync(factor);
        Assert.Equal((before.AccessFailed, before.DailyFailures), (after.AccessFailed, after.DailyFailures));
    }

    [Fact]
    public async Task TwoConcurrentCompletions_GiveOneSession_AndAnOlderChallengeDies()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);

        var answers = await Task.WhenAll(PostCodeAsync(pending.Challenge, CodeAt(factor, 1)), PostCodeAsync(pending.Challenge, CodeAt(factor, 2)));
        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromSeconds(90));
        var older = await factory.PasswordStepAsync(email);
        var newer = await factory.PasswordStepAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(newer.Challenge, CodeAt(factor, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostCodeAsync(older.Challenge, CodeAt(factor, 1))).StatusCode);
    }

    [Fact]
    public async Task BackToBackSignIns_OnAFrozenClock_AndAStepUpAfterALogin_AreAccepted()
    {
        var (email, factor) = await SeedEnrolledAsync();
        factory.Clock.Freeze();

        await factory.LoginAsync(email);
        await factory.LoginAsync(email);
        Assert.Equal(2, (await CountersAsync(factor)).SignIns);

        Assert.IsType<FactorOutcome.Accepted>(
            await VerifyInOwnScopeAsync(factor, Parsed(factor.RecoveryCodes[0]), await StepUpBindingAsync(factor)));
    }

    // Review round 1, item 9: states the writes never leave fail closed, never 500.
    [Fact]
    public async Task AKeyThatDecryptsButIsNotBase32_IsUnavailable_NotAServerError()
    {
        var (email, factor) = await SeedEnrolledAsync();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(factor.AccountId);
            var credentials = scope.ServiceProvider.GetRequiredService<TwoFactorCredentials>();
            await credentials.StageProtectedAsync(factor.UserId, ProtectedTwoFactorValue.AuthenticatorKey, "not base32!", default);
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        }
        factory.Sink.Events.Clear();

        var answer = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, CodeAt(factor, 1));

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.KeyUnavailable), (answer.StatusCode, (await ProblemOf(answer)).Title));
        Assert.Single(factory.EventsFor(SecurityEvents.SecondFactorKeyUnreadable));
    }

    [Fact]
    public async Task ASpentBudgetWithNoWindowStart_IsThrottled_NotAServerError()
    {
        var (email, factor) = await SeedEnrolledAsync();
        await SetBudgetAsync(factor, failures: 20, since: null);

        var answer = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, CodeAt(factor, 1));

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.True(int.Parse(answer.Headers.GetValues("Retry-After").Single()) >= 1);
    }

    [Fact]
    public async Task AWindowThatEndsThisSecond_AsksTheClientToWaitAtLeastOneSecond()
    {
        var (email, factor) = await SeedEnrolledAsync();
        factory.Clock.Freeze();
        await SetBudgetAsync(factor, failures: 20, since: factory.Clock.GetUtcNow() - TimeSpan.FromHours(24));

        var answer = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, CodeAt(factor, 1));

        Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
        Assert.Equal("1", answer.Headers.GetValues("Retry-After").Single());
    }

    private async Task SetBudgetAsync(EnrolledFactor factor, int failures, DateTimeOffset? since)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == factor.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorFailures, failures).SetProperty(u => u.SecondFactorFailuresSince, since));
    }
}
