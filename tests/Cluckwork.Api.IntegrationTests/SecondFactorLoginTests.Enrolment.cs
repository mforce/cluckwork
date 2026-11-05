using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1277: the enrolment routes (#1274 "Enrolling and replacing a key"), G2.1 to
// G2.5, the confirm-pair case of G3.31, and the stale-session rule that
// turn-off set (#1280).
public sealed partial class SecondFactorLoginTests
{
    private const string TwoFactorPath = "/api/v1/auth/two-factor";
    private const string StartPath = TwoFactorPath + "/start";
    private const string ConfirmPath = TwoFactorPath + "/confirm";
    private const string RecoveryCodesPath = TwoFactorPath + "/recovery-codes";

    private sealed record StartedEnrolment(string Key, string OtpauthUri);

    private static Task<HttpResponseMessage> PostWithGrantAsync(HttpClient client, string path, string grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string code) =>
        client.PostWithKeyAsync(ConfirmPath, Guid.NewGuid().ToString(), new { code });

    private static async Task<StartedEnrolment> ReadStartAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new StartedEnrolment(body.GetProperty("key").GetString()!, body.GetProperty("otpauthUri").GetString()!);
    }

    private string CurrentCode(string key) =>
        TotpMatcher.Compute(TotpMatcher.DecodeBase32(key), TotpMatcher.StepAt(factory.Clock.GetUtcNow())).ToString("D6");

    private async Task<StartedEnrolment> StartWithPasswordAsync(HttpClient client) =>
        await ReadStartAsync(await PostWithGrantAsync(client, StartPath, (await GrantOf(await StepUpAsync(client, TestHarness.Password)))!));

    // Six digits that match the key at no step from -3 to +3.
    private string WrongCodeFor(string key)
    {
        var near = Enumerable.Range(-3, 7)
            .Select(offset => TotpMatcher.Compute(TotpMatcher.DecodeBase32(key), TotpMatcher.StepAt(factory.Clock.GetUtcNow()) + offset))
            .ToHashSet();
        return Enumerable.Range(0, 1_000_000).First(code => !near.Contains(code)).ToString("D6");
    }

    private static async Task<(bool Enabled, int Left)> StatusOf(HttpClient client)
    {
        var response = await client.GetAsync(TwoFactorPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("enabled").GetBoolean(), body.GetProperty("recoveryCodesLeft").GetInt32());
    }

    private static async Task<(HttpStatusCode Status, string? Title)> TitleOf(HttpResponseMessage response)
    {
        var (status, title, _) = await ProblemOf(response);
        return (status, title);
    }

    private static string[] RecoveryCodesOf(JsonElement body) =>
        [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!)];

    // An unenrolled user, signed in, with the HTTP enrolment's key and codes
    // registered so the harness signs them in with a code from then on.
    private async Task<(string Email, Guid AccountId, Guid UserId, HttpClient Client, TokenPairDto Session)> SignedInPlainAsync()
    {
        var email = $"tf-enrol-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var session = await factory.LoginAsync(email);
        return (email, accountId, await UserIdAsync(accountId, email), factory.CreateAuthedClient(session.AccessToken), session);
    }

    private async Task<int> PendingRowsAsync(Guid accountId, Guid userId) =>
        (await TwoFactorRowsAsync(accountId, userId)).Count(name => name == "PendingAuthenticator");

    // G2.1, with confirm's events.
    [Fact]
    public async Task EnrollingThroughTheRoutes_TurnsTwoFactorOn_AndKeepsOnlyTheActingSession()
    {
        var (email, accountId, userId, client, acting) = await SignedInPlainAsync();
        var other = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, other.AccessToken);
        Assert.Equal((false, 0), await StatusOf(client));
        factory.Sink.Events.Clear();

        var start = await StartWithPasswordAsync(client);
        var response = await ConfirmAsync(client, CurrentCode(start.Key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var codes = RecoveryCodesOf(body);
        Assert.Equal(10, codes.Length);
        var fresh = factory.CreateAuthedClient(body.GetProperty("accessToken").GetString()!);
        Assert.Equal((true, 10), await StatusOf(fresh));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(accountId, TestHarness.ExtractRefreshCookie(response, accountId))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(acting.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(accountId, other.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        await AssertEverySessionAndAppEndedAsync(accountId, userId, freshRefreshRows: 1);
        Assert.Equal(SecondFactorErrorCodes.Required, await factory.LoginRefusalTitleAsync(email, TestHarness.Password));
        var row = Assert.Single(await AuditRowsAsync(accountId, userId, AuditActions.UserTwoFactorEnabled));
        Assert.Equal(userId, row.Actor);
        Assert.False(JsonDocument.Parse(row.Details!).RootElement.GetProperty("replaced").GetBoolean());

        foreach (var securityEvent in new[] { SecurityEvents.TwoFactorEnrolmentStarted, SecurityEvents.TwoFactorEnrolled })
        {
            var emitted = Assert.Single(factory.EventsFor(securityEvent));
            Assert.StartsWith("`UserId`, `ClientIp`", PolicyFieldsFor(securityEvent));
            // TenantResolutionMiddleware's scope puts the farm on every event
            // of a signed-in request.
            Assert.Equal(["ClientIp", "SecurityEvent", "UserId"],
                emitted.Properties.Keys.Where(key => !HostMetadata.Contains(key) && key != "AccountId").Order());
        }
        // A key, its URI and a code are credentials: nothing logged during the
        // enrolment holds one.
        foreach (var logged in factory.Sink.Events)
        {
            var everything = $"{logged.RenderMessage()} {string.Join(" ", logged.Properties.Select(p => $"{p.Key}={p.Value}"))} {logged.Exception}";
            foreach (var secret in codes.Append(start.Key).Append(start.OtpauthUri))
                Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    // G3.31, the case only confirm's route can mint: its pair's lineage starts
    // at the confirm.
    [Fact]
    public async Task ConfirmsFreshPair_RefreshesUntilTheCap_CountedFromTheConfirm()
    {
        var (_, accountId, _, client, _) = await SignedInPlainAsync();
        var start = await StartWithPasswordAsync(client);
        var confirmed = await ConfirmAsync(client, CurrentCode(start.Key));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        factory.Clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        var lastInside = await RefreshAsync(accountId, TestHarness.ExtractRefreshCookie(confirmed, accountId));
        Assert.Equal(HttpStatusCode.OK, lastInside.StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var refused = await RefreshAsync(accountId, TestHarness.ExtractRefreshCookie(lastInside, accountId));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Identity.ExpiredRefreshToken", (await TestHarness.ReadProblemAsync(refused)).GetProperty("title").GetString());
    }

    // G2.2
    [Fact]
    public async Task RecoveryCodes_AppearOnlyInTheFirstConfirm_AndAreNeverStored()
    {
        var (email, accountId, userId, client, _) = await SignedInPlainAsync();
        var start = await StartWithPasswordAsync(client);
        var first = await ConfirmAsync(client, CurrentCode(start.Key));
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        var codes = RecoveryCodesOf(body);
        factory.EnrolledFactors[email] = new EnrolledFactor(accountId, userId, start.Key, codes);
        var fresh = factory.CreateAuthedClient(body.GetProperty("accessToken").GetString()!);

        var status = await fresh.GetAsync(TwoFactorPath);
        var grant = await GrantOf(await StepUpAsync(fresh, TestHarness.Password, codes[0]));
        var replacement = await ReadStartAsync(await PostWithGrantAsync(fresh, StartPath, grant!));
        var replaced = await ConfirmAsync(fresh, CurrentCode(replacement.Key));

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var replacedBody = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(replacedBody.TryGetProperty("recoveryCodes", out _), "a replacement returned recovery codes");
        var later = new[] { await status.Content.ReadAsStringAsync(), replacedBody.GetRawText() };
        var stored = await factory.WithTenantScopeAsync(accountId, db => db.UserTokens
            .Where(t => t.UserId == userId).Select(t => new { t.Name, t.Value }).ToListAsync());
        Assert.Equal(9, stored.Count(row => row.Name.StartsWith("RecoveryCode:", StringComparison.Ordinal)));
        foreach (var code in codes)
        {
            var bare = code.Replace("-", "", StringComparison.Ordinal);
            Assert.All(later, text => Assert.DoesNotContain(bare, text.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
            Assert.All(stored, row => Assert.DoesNotContain(bare, $"{row.Name}{row.Value}".Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
        }
        var replacedFlags = (await AuditRowsAsync(accountId, userId, AuditActions.UserTwoFactorEnabled))
            .Select(r => JsonDocument.Parse(r.Details!).RootElement.GetProperty("replaced").GetBoolean());
        Assert.Equal([false, true], replacedFlags.Order());
    }

    // G2.3
    [Fact]
    public async Task FiveWrongConfirms_OverHttp_EndTheEnrolment()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync();
        var start = await StartWithPasswordAsync(client);
        var wrong = WrongCodeFor(start.Key);
        var records = await IdempotencyRecordsAsync(accountId);

        var attemptsLeft = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var response = await ConfirmAsync(client, wrong);
            Assert.Equal((HttpStatusCode.BadRequest, EnrolmentErrorCodes.CodeIncorrect), await TitleOf(response));
            attemptsLeft.Add((await TestHarness.ReadProblemAsync(response)).GetProperty("attemptsLeft").GetInt32());
        }
        var afterFifth = await ConfirmAsync(client, CurrentCode(start.Key));

        Assert.Equal([4, 3, 2, 1, 0], attemptsLeft);
        Assert.Equal((HttpStatusCode.Conflict, EnrolmentErrorCodes.Expired), await TitleOf(afterFifth));
        Assert.Equal(0, await PendingRowsAsync(accountId, userId));
        Assert.Equal(records, await IdempotencyRecordsAsync(accountId));
        Assert.False((await FactorStateAsync(accountId, userId)).Enabled);
    }

    // A wrong confirm's count commits with no request token, so a client that
    // hangs up after the check cannot roll its attempt back (#1274
    // "Transactions").
    [Fact]
    public async Task AWrongConfirm_StillCounts_WhenTheClientHangsUpBeforeTheCommit()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync();
        var start = await StartWithPasswordAsync(client);
        using var cancel = new CancellationTokenSource();
        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, userId))
        {
            var identity = ActivatorUtilities.CreateInstance<IdentityProvider>(scope.ServiceProvider,
                new CancelAfterCheck(scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>(), cancel));

            var result = await identity.ConfirmTwoFactorEnrolmentAsync(accountId, userId, WrongCodeFor(start.Key), cancel.Token);

            Assert.True(cancel.IsCancellationRequested, "the request was not cancelled after the confirm");
            Assert.Equal(4, Assert.IsType<EnrolmentCodeIncorrectError>(result.Error).AttemptsLeft);
        }
        var next = await ConfirmAsync(client, WrongCodeFor(start.Key));
        Assert.Equal(3, (await TestHarness.ReadProblemAsync(next)).GetProperty("attemptsLeft").GetInt32());
    }

    // Contract item 7: a malformed code cannot be a guess, so it counts nothing.
    [Theory]
    [InlineData("12345")]
    [InlineData("recovery")]
    public async Task AMalformedConfirmCode_Is400WithoutAttemptsLeft_AndCountsNothing(string shape)
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync();
        var start = await StartWithPasswordAsync(client);
        // A recovery code's shape parses, but is not an authenticator code.
        var malformed = shape == "recovery" ? "ABCD-EFGH-JKMN-PQRT" : shape;

        var response = await ConfirmAsync(client, malformed);

        Assert.Equal((HttpStatusCode.BadRequest, EnrolmentErrorCodes.CodeIncorrect), await TitleOf(response));
        Assert.False((await TestHarness.ReadProblemAsync(response)).TryGetProperty("attemptsLeft", out _));
        Assert.Equal(1, await PendingRowsAsync(accountId, userId));
        var wrong = await ConfirmAsync(client, WrongCodeFor(start.Key));
        Assert.Equal(4, (await TestHarness.ReadProblemAsync(wrong)).GetProperty("attemptsLeft").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, CurrentCode(start.Key))).StatusCode);
    }

    [Fact]
    public async Task ConfirmWithNoStartedEnrolment_Is409_AndStartWithoutAGrant_Is403()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync();

        var confirm = await ConfirmAsync(client, "123456");
        var start = await PostWithGrantAsync(client, StartPath, "not-a-grant");

        Assert.Equal((HttpStatusCode.Conflict, EnrolmentErrorCodes.Expired), await TitleOf(confirm));
        Assert.Equal((HttpStatusCode.Forbidden, StepUpErrorCodes.Required), await TitleOf(start));
        Assert.Equal(0, await PendingRowsAsync(accountId, userId));
    }

    // A replacement and a regenerate each spend a grant that proved a code, so
    // a missing or bad grant changes nothing.
    [Theory]
    [InlineData(StartPath, null)]
    [InlineData(StartPath, "not-a-grant")]
    [InlineData(RecoveryCodesPath, null)]
    [InlineData(RecoveryCodesPath, "not-a-grant")]
    public async Task AnEnrolledUsersStartOrRegenerate_WithoutAValidGrant_Is403_AndChangesNothing(string path, string? grant)
    {
        var (_, factor, client) = await SignedInEnrolledAsync();
        var before = await TwoFactorRowsAsync(factor.AccountId, factor.UserId);

        var response = grant is null
            ? await client.PostWithKeyAsync(path, Guid.NewGuid().ToString())
            : await PostWithGrantAsync(client, path, grant);

        Assert.Equal((HttpStatusCode.Forbidden, StepUpErrorCodes.Required), await TitleOf(response));
        Assert.Equal(before.Order(), (await TwoFactorRowsAsync(factor.AccountId, factor.UserId)).Order());
        Assert.Empty(await AuditRowsAsync(factor.AccountId, factor.UserId, AuditActions.UserRecoveryCodesRegenerated));
    }

    // A grant made with the password alone, before the user enrolled, died
    // with the stamp the enrolment rotated: it cannot start a replacement.
    [Fact]
    public async Task APasswordOnlyGrant_FromBeforeAnEnrolment_CannotStartAReplacement()
    {
        var (email, accountId, userId, client, _) = await SignedInPlainAsync();
        var earlier = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var other = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var start = await StartWithPasswordAsync(other);
        var confirmed = await ConfirmAsync(other, CurrentCode(start.Key));
        var fresh = factory.CreateAuthedClient((await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        var response = await PostWithGrantAsync(fresh, StartPath, earlier!);

        Assert.Equal((HttpStatusCode.Forbidden, StepUpErrorCodes.Required), await TitleOf(response));
        Assert.Equal(0, await PendingRowsAsync(accountId, userId));
    }

    // G3.23 over HTTP: two confirms of one payload, the second sent while the
    // first waits at the user-row lock, give one enrolment and one set of codes.
    [Fact]
    public async Task TwoConcurrentConfirms_OfOnePayload_EnrolOnce()
    {
        var (_, accountId, userId, client, acting) = await SignedInPlainAsync();
        var other = factory.CreateAuthedClient(acting.AccessToken);
        var start = await StartWithPasswordAsync(client);
        var code = CurrentCode(start.Key);
        var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            reachedLock.SetResult();
            await resumeFirst.Task;
        });

        var first = ConfirmAsync(client, code);
        await reachedLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
        HttpResponseMessage second;
        try
        {
            second = await ConfirmAsync(other, code);
        }
        finally
        {
            resumeFirst.TrySetResult();
        }
        HttpResponseMessage[] responses = [await first, second];

        var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict);
        Assert.Equal(10, RecoveryCodesOf(await success.Content.ReadFromJsonAsync<JsonElement>()).Length);
        Assert.Single(await AuditRowsAsync(accountId, userId, AuditActions.UserTwoFactorEnabled));
        Assert.Equal(0, await PendingRowsAsync(accountId, userId));
        Assert.Equal(10, (await TwoFactorRowsAsync(accountId, userId)).Count(n => n.StartsWith("RecoveryCode:", StringComparison.Ordinal)));
    }

    // #530: a session of farm B naming farm A's user reads nothing and changes
    // nothing, because every read and lock filters by the account as well as
    // the user (GetStatusAsync, ActingSessionIsCurrentAsync, the owner's lock).
    [Fact]
    public async Task AnotherFarmsSession_CannotReadOrChangeAUsersEnrolment()
    {
        var (_, victim) = await SeedEnrolledAsync();
        await StartEnrolmentAsync(victim.AccountId, victim.UserId);
        var rowsBefore = (await TwoFactorRowsAsync(victim.AccountId, victim.UserId)).Order().ToList();
        var epoch = (await FactorStateAsync(victim.AccountId, victim.UserId)).Epoch;
        var (_, otherAccount, otherUser, _, _) = await SignedInPlainAsync();
        using var scope = factory.Services.CreateScope().ResolveTenantAndActor(otherAccount, otherUser);
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        var status = await identity.GetTwoFactorStatusAsync(otherAccount, victim.UserId);
        var started = await identity.StartTwoFactorEnrolmentAsync(otherAccount, victim.UserId, epoch);
        var regenerated = await identity.RegenerateRecoveryCodesAsync(otherAccount, victim.UserId, epoch);
        var confirmed = await identity.ConfirmTwoFactorEnrolmentAsync(otherAccount, victim.UserId, CodeAt(victim, 0));

        Assert.Equal(new TwoFactorStatus(false, 0), status);
        Assert.Equal(CredentialErrorCodes.Superseded, started.Error.Code);
        Assert.Equal(CredentialErrorCodes.Superseded, regenerated.Error.Code);
        Assert.Equal(EnrolmentErrorCodes.Expired, confirmed.Error.Code);
        Assert.Equal(rowsBefore, (await TwoFactorRowsAsync(victim.AccountId, victim.UserId)).Order());
        Assert.Equal(epoch, (await FactorStateAsync(victim.AccountId, victim.UserId)).Epoch);
    }

    // A plain 401 makes the SPA refresh and resend, so a grant-spending route
    // answers 401 only for a session that went stale under it.
    [Theory]
    [InlineData(StepUpErrorCodes.Required, HttpStatusCode.Forbidden)]
    [InlineData(CredentialErrorCodes.Superseded, HttpStatusCode.Unauthorized)]
    [InlineData(EnrolmentErrorCodes.NotEnabled, HttpStatusCode.Conflict)]
    public void AGrantSpendingRoutesRefusal_HasItsOwnStatus(string code, HttpStatusCode status)
    {
        var answer = AuthEndpoints.StepUpRouteProblem(new Error(code, "refused"));

        Assert.Equal((int)status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(answer).StatusCode);
    }

    [Fact]
    public void AGrantSpendingRoutesUnmappedError_IsNeverA401()
    {
        Assert.Throws<InvalidOperationException>(() => AuthEndpoints.StepUpRouteProblem(new Error("Users.Conflict", "unexpected")));
    }

    // G2.4
    [Fact]
    public async Task AStartedReplacement_LeavesTheLiveKeyWorking_AndARecoveryCodeGrantCompletesIt()
    {
        // Recovery codes sign in and step up, so the live key's watermark
        // leaves room in the window for one code from each key below.
        var (email, factor) = await SeedEnrolledAsync();
        var signedIn = await factory.LoginWithSecondFactorAsync(email, factor.RecoveryCodes[1]);
        var client = factory.CreateAuthedClient((await TestHarness.ReadTokensAsync(signedIn)).AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var replacement = await ReadStartAsync(await PostWithGrantAsync(client, StartPath, grant!));

        Assert.Equal(HttpStatusCode.OK, (await factory.LoginWithSecondFactorAsync(email)).StatusCode);

        var confirmed = await ConfirmAsync(client, CurrentCode(replacement.Key));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var oldKeyCode = await factory.NextTotpCodeAsync(email);
        factory.EnrolledFactors[email] = factor with { Key = replacement.Key };
        var newKeyCode = await factory.NextTotpCodeAsync(email);
        var pending = await factory.PasswordStepAsync(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCodeAsync(pending.Challenge, oldKeyCode)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, newKeyCode)).StatusCode);
        var fresh = factory.CreateAuthedClient((await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);
        Assert.Equal((true, 8), await StatusOf(fresh));
    }

    // Regenerate replaces every code and ends no session.
    [Fact]
    public async Task RegeneratingRecoveryCodes_ReplacesThem_AndKeepsEverySession()
    {
        var (email, factor, client) = await SignedInEnrolledAsync();
        var other = await factory.LoginAsync(email);
        var epoch = (await FactorStateAsync(factor.AccountId, factor.UserId)).Epoch;
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var records = await IdempotencyRecordsAsync(factor.AccountId);

        var response = await PostWithGrantAsync(client, RecoveryCodesPath, grant!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var codes = RecoveryCodesOf(await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(10, codes.Distinct().Count());
        Assert.Empty(codes.Intersect(factor.RecoveryCodes));
        Assert.Equal(records, await IdempotencyRecordsAsync(factor.AccountId));
        Assert.Equal(epoch, (await FactorStateAsync(factor.AccountId, factor.UserId)).Epoch);
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(other.AccessToken)).StatusCode);
        Assert.Equal((true, 10), await StatusOf(client));
        var row = Assert.Single(await AuditRowsAsync(factor.AccountId, factor.UserId, AuditActions.UserRecoveryCodesRegenerated));
        Assert.Null(row.Details);
        var pending = await factory.PasswordStepAsync(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCodeAsync(pending.Challenge, factor.RecoveryCodes[1])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, codes[0])).StatusCode);
    }

    // Contract item 8.
    [Fact]
    public async Task RegeneratingRecoveryCodes_WhenTwoFactorIsOff_Is409_AndWritesNothing()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync();
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));

        var response = await PostWithGrantAsync(client, RecoveryCodesPath, grant!);

        Assert.Equal((HttpStatusCode.Conflict, EnrolmentErrorCodes.NotEnabled), await TitleOf(response));
        Assert.Empty(await TwoFactorRowsAsync(accountId, userId));
        Assert.Empty(await AuditRowsAsync(accountId, userId, AuditActions.UserRecoveryCodesRegenerated));
    }

    // Contract item 5: start and regenerate spend the grant before the
    // user-row lock, so a password reset that commits in between leaves the
    // asking session stale. It gets no key and no codes, and nothing changes.
    [Theory]
    [InlineData(StartPath)]
    [InlineData(RecoveryCodesPath)]
    public async Task StartAndRegenerate_AfterTheSessionWentStaleUnderTheRequest_AreRefused(string path)
    {
        var (accountId, ownerId, _) = await FarmWithOwnerAsync();
        var (email, factor) = await EnrolledManagerAsync(accountId);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var rowsBefore = await TwoFactorRowsAsync(accountId, factor.UserId);
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, ownerId);
            var done = await scope.ServiceProvider.GetRequiredService<IIdentityProvider>()
                .SetUserPasswordAsync(accountId, factor.UserId, NewPassword);
            Assert.True(done.IsSuccess);
        });

        var response = await PostWithGrantAsync(client, path, grant!);

        Assert.True(factory.BeforeCommand.Fired, "the concurrent reset never ran");
        Assert.Equal((HttpStatusCode.Unauthorized, CredentialErrorCodes.Superseded), await TitleOf(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("recoveryCodes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("otpauth", body, StringComparison.Ordinal);
        Assert.Equal(rowsBefore.Order(), (await TwoFactorRowsAsync(accountId, factor.UserId)).Order());
        Assert.Empty(await AuditRowsAsync(accountId, factor.UserId, AuditActions.UserRecoveryCodesRegenerated));
    }

    // G2.5: every route under the prefix, plus the code step and step-up,
    // answers a successful request with an Idempotency-Key and stores no
    // record. The walk reads the mapped routes, so a new two-factor route
    // fails here until it has a request below.
    [Fact]
    public async Task EveryTwoFactorRoute_TheCodeStepAndStepUp_StoreNoIdempotencyRecord()
    {
        var walked = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.StartsWith(TwoFactorPath, StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Append($"POST {StepUpPath}").Append($"POST {TestHarness.SecondFactorPath}")
            .ToHashSet();
        Assert.True(walked.Count >= 7, $"the walk found {walked.Count} routes, below its floor of 7");

        var (email, accountId, userId, client, _) = await SignedInPlainAsync();
        var records = await IdempotencyRecordsAsync(accountId);
        var sent = new Dictionary<string, HttpStatusCode>();
        async Task<HttpResponseMessage> Send(string route, Task<HttpResponseMessage> request)
        {
            var response = await request;
            sent[route] = response.StatusCode;
            return response;
        }

        await Send($"GET {TwoFactorPath}", client.GetAsync(TwoFactorPath));
        var start = await ReadStartAsync(await Send($"POST {StartPath}", PostWithGrantAsync(client, StartPath,
            (await GrantOf(await Send($"POST {StepUpPath}", StepUpAsync(client, TestHarness.Password))))!)));
        var confirmed = await (await Send($"POST {ConfirmPath}", ConfirmAsync(client, CurrentCode(start.Key)))).Content.ReadFromJsonAsync<JsonElement>();
        var codes = RecoveryCodesOf(confirmed);
        factory.EnrolledFactors[email] = new EnrolledFactor(accountId, userId, start.Key, codes);
        client = factory.CreateAuthedClient(confirmed.GetProperty("accessToken").GetString()!);
        var regenerated = await Send($"POST {RecoveryCodesPath}", PostWithGrantAsync(client, RecoveryCodesPath,
            (await GrantOf(await StepUpAsync(client, TestHarness.Password, codes[0])))!));
        codes = RecoveryCodesOf(await regenerated.Content.ReadFromJsonAsync<JsonElement>());
        var pending = await factory.PasswordStepAsync(email);
        await Send($"POST {TestHarness.SecondFactorPath}", factory.CreateClient(TestHarness.Cookieless(factory))
            .PostWithKeyAsync(TestHarness.SecondFactorPath, Guid.NewGuid().ToString(), new { challenge = pending.Challenge, code = codes[1] }));
        await Send($"DELETE {TwoFactorPath}", DeleteWithGrantAsync(client, TwoFactorPath,
            (await GrantOf(await StepUpAsync(client, TestHarness.Password, codes[2])))!));

        Assert.Equal(walked.Order(), sent.Keys.Order());
        Assert.All(sent, route => Assert.True((int)route.Value is >= 200 and < 300, $"{route.Key} answered {(int)route.Value}"));
        Assert.Equal(records, await IdempotencyRecordsAsync(accountId));
    }
}
