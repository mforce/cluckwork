using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #1279: an enrolled user steps up with the password and a code (#1274
// decisions 5, 11, 12 and 14), guards G4.1 to G4.5 at /auth/step-up.
public sealed partial class SecondFactorLoginTests
{
    private const string StepUpPath = "/api/v1/auth/step-up";

    // An enrolled user signed in through the code step, with that session's client.
    private async Task<(string Email, EnrolledFactor Factor, HttpClient Client)> SignedInEnrolledAsync()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var session = await factory.LoginAsync(email);
        return (email, factor, factory.CreateAuthedClient(session.AccessToken));
    }

    // With an Idempotency-Key, so the route's not-cacheable entry is what keeps
    // a refused attempt's charge (G4.1).
    private static Task<HttpResponseMessage> StepUpAsync(HttpClient client, string password, string? code = null) =>
        client.PostWithKeyAsync(StepUpPath, Guid.NewGuid().ToString(),
            code is null ? new { password } : new { password, code });

    private static async Task<string?> GrantOf(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK
            ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()
            : null;

    private async Task<Result> ValidateGrantAsync(Guid accountId, Guid userId, string grant)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IStepUpGrantService>().ValidateAsync(accountId, userId, grant);
    }

    [Fact]
    public async Task AnEnrolledUser_StepsUpWithThePasswordAndACode_AndGetsAPasswordGrantsShape()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var unenrolledEmail = $"tf-plain-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(unenrolledEmail);
        var unenrolled = factory.CreateAuthedClient((await factory.LoginAsync(unenrolledEmail)).AccessToken);

        var withCode = await GrantOf(await StepUpAsync(client, TestHarness.Password, CodeAt(factor, 2)));
        var withPassword = await GrantOf(await StepUpAsync(unenrolled, TestHarness.Password));

        Assert.NotNull(withCode);
        Assert.NotNull(withPassword);
        static (string, string) Shape(string grant)
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(grant);
            return (jwt.Audiences.Single(), string.Join(' ', jwt.Claims.Select(c => c.Type).Order(StringComparer.Ordinal)));
        }
        Assert.Equal(Shape(withPassword), Shape(withCode));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
    }

    // G4.2
    [Fact]
    public async Task ThePasswordAlone_ForAnEnrolledUser_Is400SecondFactorRequired_AndChargesNothing()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var before = await CountersAsync(factor);

        var response = await StepUpAsync(client, TestHarness.Password);

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Required),
            (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(before, await CountersAsync(factor));
    }

    [Fact]
    public async Task AWrongOrMalformedCode_AfterTheRightPassword_IsOne400Body_AndOnlyTheWrongOneIsCharged()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();

        var malformed = await ProblemOf(await StepUpAsync(client, TestHarness.Password, "12345"));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
        var wrong = await ProblemOf(await StepUpAsync(client, TestHarness.Password, WrongCode(factor)));

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (wrong.Status, wrong.Title));
        Assert.Equal(wrong, malformed);
        var counters = await CountersAsync(factor);
        Assert.Equal((1, 1), (counters.AccessFailed, counters.DailyFailures));
    }

    [Fact]
    public async Task AWrongPassword_WithTheRightCode_IsAWrongPassword_AndTheCodeIsNotChecked()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var code = CodeAt(factor, 2);

        var wrong = await ProblemOf(await StepUpAsync(client, WrongPassword, code));

        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (wrong.Status, wrong.Title));
        Assert.NotNull(await GrantOf(await StepUpAsync(client, TestHarness.Password, code)));
    }

    // G4.1
    [Fact]
    public async Task FiveWrongCodes_WithTheRightPassword_LockTheAccount()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await StepUpAsync(client, TestHarness.Password, WrongCode(factor))).StatusCode);
        Assert.Null((await CountersAsync(factor)).LockoutEnd);
        await StepUpAsync(client, TestHarness.Password, WrongCode(factor));

        Assert.NotNull((await CountersAsync(factor)).LockoutEnd);
        var locked = await ProblemOf(await StepUpAsync(client, TestHarness.Password, CodeAt(factor, 2)));
        Assert.Equal((HttpStatusCode.BadRequest, "Users.CurrentPasswordIncorrect"), (locked.Status, locked.Title));
    }

    [Fact]
    public async Task FourWrongCodesAtLogin_APasswordOnlyStepUp_AndOneWrongCode_LockTheAccount()
    {
        var (email, factor, client) = await SignedInEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        for (var i = 0; i < 4; i++) await PostCodeAsync(pending.Challenge, WrongCode(factor));

        Assert.Equal(HttpStatusCode.BadRequest, (await StepUpAsync(client, TestHarness.Password)).StatusCode);
        Assert.Equal(4, (await CountersAsync(factor)).AccessFailed);
        await StepUpAsync(client, TestHarness.Password, WrongCode(factor));

        Assert.NotNull((await CountersAsync(factor)).LockoutEnd);
    }

    // The enrolled twin of StepUp_LockoutTripsOnTheFifthUserAttempt_NotTheThird.
    [Fact]
    public async Task WrongPasswords_LockAnEnrolledUser_OnTheFifth_AndOnlyAnAcceptedCodeResetsTheCount()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        for (var i = 0; i < 4; i++) await StepUpAsync(client, WrongPassword);
        Assert.Equal(4, (await CountersAsync(factor)).AccessFailed);

        Assert.NotNull(await GrantOf(await StepUpAsync(client, TestHarness.Password, CodeAt(factor, 2))));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);

        for (var i = 0; i < 4; i++) await StepUpAsync(client, WrongPassword);
        Assert.Null((await CountersAsync(factor)).LockoutEnd);
        await StepUpAsync(client, WrongPassword);
        Assert.NotNull((await CountersAsync(factor)).LockoutEnd);
    }

    // G4.3: the grant a code mints is the one ValidateAsync already checks.
    [Fact]
    public async Task AGrantMintedWithACode_IsSingleUse_AndBoundToTheStampTheLogoutEpochAndTheAccount()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        async Task<string> MintAsync(string code) =>
            (await GrantOf(await StepUpAsync(client, TestHarness.Password, code)))!;

        var replayed = await MintAsync(CodeAt(factor, 2));
        Assert.True((await ValidateGrantAsync(factor.AccountId, factor.UserId, replayed)).IsSuccess);
        Assert.False((await ValidateGrantAsync(factor.AccountId, factor.UserId, replayed)).IsSuccess);

        var foreign = await MintAsync(factor.RecoveryCodes[0]);
        Assert.False((await ValidateGrantAsync(Guid.NewGuid(), factor.UserId, foreign)).IsSuccess);

        var loggedOut = await MintAsync(factor.RecoveryCodes[1]);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IIdentityProvider>().RecordLogoutAsync(factor.UserId);
        Assert.False((await ValidateGrantAsync(factor.AccountId, factor.UserId, loggedOut)).IsSuccess);

        var stamped = await MintAsync(factor.RecoveryCodes[2]);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByIdAsync(factor.UserId.ToString()))!)).Succeeded);
        }
        Assert.False((await ValidateGrantAsync(factor.AccountId, factor.UserId, stamped)).IsSuccess);
    }

    // G4.4
    [Fact]
    public async Task TwoConcurrentStepUps_WithOneCode_GiveOneGrant()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var code = CodeAt(factor, 2);

        var answers = await Task.WhenAll(
            StepUpAsync(client, TestHarness.Password, code), StepUpAsync(client, TestHarness.Password, code));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ACodeAcceptedAtLogin_IsRefusedAtStepUp()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var code = CodeAt(factor, 2);
        var signedIn = await PostCodeAsync((await factory.PasswordStepAsync(email)).Challenge, code);
        var client = factory.CreateAuthedClient((await TestHarness.ReadTokensAsync(signedIn)).AccessToken);

        var reuse = await StepUpAsync(client, TestHarness.Password, code);

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (reuse.StatusCode, (await ProblemOf(reuse)).Title));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
    }

    // G3.6 at step-up, where the password check tracks the user before the lock.
    [Fact]
    public async Task EightConcurrentWrongCodesAtStepUp_ChargeFive_AndLockOnce()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        factory.Sink.Events.Clear();

        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => StepUpAsync(client, TestHarness.Password, WrongCode(factor))));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode));
        var counters = await CountersAsync(factor);
        Assert.Equal(0, counters.AccessFailed);
        Assert.NotNull(counters.LockoutEnd);
        Assert.Equal(5, counters.DailyFailures);
        Assert.Single(factory.EventsFor(SecurityEvents.AccountLockedOut),
            e => e.Properties["UserId"] is ScalarValue { Value: var id } && id?.ToString() == factor.UserId.ToString());
    }

    // A rehash save that loses to a concurrent writer leaves the checked user
    // Modified, which the user-row lock refuses; the reload keeps it working.
    [Fact]
    public async Task APasswordCheck_WhoseRehashSaveLost_StillStepsUp()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var weaker = new PasswordHasher<ApplicationUser>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
            var hash = weaker.HashPassword(new ApplicationUser(), TestHarness.Password);
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == factor.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash));
        }
        factory.BeforeCommand.Arm("UPDATE \"AspNetUsers\"", async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {factor.UserId}""");
        });

        var response = await StepUpAsync(client, TestHarness.Password, CodeAt(factor, 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARecoveryCode_StepsUpOnce_AndIsAudited()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();

        Assert.NotNull(await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0])));
        var again = await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]);

        Assert.Equal(SecondFactorErrorCodes.Incorrect, (await ProblemOf(again)).Title);
        Assert.Equal(9, await RecoveryCodesLeftAsync(factor));
        var details = Assert.Single(await RecoveryAuditDetailsAsync(factor));
        Assert.Equal(9, JsonDocument.Parse(details!).RootElement.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task ASpentDailyBudget_AtStepUp_Is429WithRetryAfter()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var since = factory.Clock.GetUtcNow();
        await SpendBudgetAsync(factor, since);

        var throttled = await StepUpAsync(client, TestHarness.Password, CodeAt(factor, 2));

        Assert.Equal((HttpStatusCode.TooManyRequests, new RetryLaterError(TimeSpan.Zero).Code),
            (throttled.StatusCode, (await ProblemOf(throttled)).Title));
        var retryAfter = TimeSpan.FromSeconds(double.Parse(throttled.Headers.GetValues("Retry-After").Single()));
        Assert.InRange(retryAfter, TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
    }

    // Seeded directly: twenty wrong codes over HTTP would lock the account at the fifth.
    private async Task SpendBudgetAsync(EnrolledFactor factor, DateTimeOffset since)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == factor.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorFailures, 20).SetProperty(u => u.SecondFactorFailuresSince, since));
    }

    // G4.5: a lost commit acknowledgement hands out no grant, and the code it
    // spent stays spent.
    [Fact]
    public async Task ACommitFailure_AtStepUp_Is503StepUpRetry_WithNoGrant_AndTheWatermarkStands()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var code = CodeAt(factor, 2);
        var before = await CountersAsync(factor);
        factory.CommitFault.ArmOnce();

        var response = await StepUpAsync(client, TestHarness.Password, code);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, StepUpErrorCodes.Retry), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(1, factory.CommitFault.Commits);
        Assert.True((await CountersAsync(factor)).Watermark > before.Watermark, "the commit that landed raised the watermark");
        Assert.Equal(SecondFactorErrorCodes.Incorrect,
            (await ProblemOf(await StepUpAsync(client, TestHarness.Password, code))).Title);
    }

    [Fact]
    public async Task AFailedUserRowUpdate_BeforeTheCommit_Is409_AndRollsBack_SoTheSameCodeWorks()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var code = CodeAt(factor, 2);
        var before = await CountersAsync(factor);
        factory.CommandFault.Arm("UPDATE \"AspNetUsers\"", afterExecution: false);

        var response = await StepUpAsync(client, TestHarness.Password, code);

        Assert.Equal((HttpStatusCode.Conflict, "Data conflict"), (response.StatusCode, (await ProblemOf(response)).Title));
        Assert.Equal(before, await CountersAsync(factor));
        Assert.NotNull(await GrantOf(await StepUpAsync(client, TestHarness.Password, code)));
    }

    // C1 (#1279): a caller's transaction could roll a refused outcome's charge
    // back, so the code step refuses to join one.
    [Fact]
    public async Task TheLoginCodeStep_RefusesToRunInACallersTransaction()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => TestHarness.InTransactionAsync(db,
            _ => identity.CompleteSecondFactorLoginAsync(pending.Challenge, WrongCode(factor))));

        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
    }
}
