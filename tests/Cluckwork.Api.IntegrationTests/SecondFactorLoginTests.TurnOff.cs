using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #1280: the user turns their own two-factor off (#1274 decision 8), G5.1.
public sealed partial class SecondFactorLoginTests
{
    private const string TurnOffPath = "/api/v1/auth/two-factor";

    // With an Idempotency-Key, so the not-cacheable entry is what keeps the
    // response out of idempotency_records.
    private static Task<HttpResponseMessage> DeleteWithGrantAsync(HttpClient client, string path, string grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private Task<List<string>> TwoFactorRowsAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, db => db.UserTokens
            .Where(t => t.UserId == userId && t.LoginProvider == "Cluckwork").Select(t => t.Name).ToListAsync());

    private Task<List<(Guid Actor, string? Details)>> AuditRowsAsync(Guid accountId, Guid userId, string action) =>
        factory.WithTenantScopeAsync(accountId, async db => (await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityId == userId && e.Action == action)
            .Select(e => new { e.ActorUserId, e.DetailsJson }).ToListAsync())
            .Select(e => (e.ActorUserId, e.DetailsJson)).ToList());

    private Task<int> IdempotencyRecordsAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.IdempotencyRecords.CountAsync(r => r.AccountId == accountId));

    private Task<Guid> UserIdAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());

    private Task<HttpResponseMessage> RefreshAsync(Guid accountId, string refreshToken) =>
        factory.CreateClient(TestHarness.Cookieless(factory)).PostRefreshAsync(refreshToken, expectedAccount: accountId.ToString());

    private async Task<HttpStatusCode> AppProbeAsync(string app)
    {
        using var probe = await ConnectedAppToken.Client(factory, app).GetAsync(SecondFactorFactory.OAuthProbePath);
        return probe.StatusCode;
    }

    // Decision 8: a factor change revokes every refresh row but the acting
    // session's fresh one and disconnects every connected app (#1229), so the
    // app leaves the list rather than staying listed and refused. The epoch
    // bump alone refuses them, so the 401s cannot show this. Call it before
    // a connected-app probe, which revokes the token it is refused with.
    private async Task AssertEverySessionAndAppEndedAsync(Guid accountId, Guid userId, int freshRefreshRows)
    {
        var subject = userId.ToString();
        var (authorizations, tokens, refreshRows) = await factory.WithTenantScopeAsync(accountId, async db => (
            await db.OAuthAuthorizations.CountAsync(a => a.Subject == subject && a.Status == Statuses.Valid),
            await db.OAuthTokens.CountAsync(t => t.Subject == subject && t.Status == Statuses.Valid),
            await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null)));
        using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, userId);
        var listed = await scope.ServiceProvider.GetRequiredService<IAccessModule>().ListConnectedAppsAsync(userId, CancellationToken.None);

        Assert.True(authorizations == 0, $"{authorizations} authorization(s) stayed valid");
        Assert.True(tokens == 0, $"{tokens} connected-app token(s) stayed valid");
        Assert.Empty(listed);
        Assert.Equal(freshRefreshRows, refreshRows);
    }

    // A started enrolment, so a clear has a pending payload to delete.
    private async Task StartEnrolmentAsync(Guid accountId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var credentials = scope.ServiceProvider.GetRequiredService<ITwoFactorCredentials>();
        await TestHarness.InTransactionAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            ct => credentials.StartEnrolmentAsync(accountId, userId, ct));
    }

    // G5.1
    [Fact]
    public async Task TurningTwoFactorOff_EndsTheFactorAndEveryOtherSession_AndKeepsTheActingOne()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var other = await factory.LoginAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, other.AccessToken, factor.RecoveryCodes[1]);
        var acting = await factory.LoginAsync(email);
        await StartEnrolmentAsync(factor.AccountId, factor.UserId);
        var client = factory.CreateAuthedClient(acting.AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var records = await IdempotencyRecordsAsync(factor.AccountId);

        var response = await DeleteWithGrantAsync(client, TurnOffPath, grant!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = await TestHarness.ReadTokensAsync(response);
        Assert.Equal(records, await IdempotencyRecordsAsync(factor.AccountId));
        Assert.Empty(await TwoFactorRowsAsync(factor.AccountId, factor.UserId));
        Assert.Single(await AuditRowsAsync(factor.AccountId, factor.UserId, AuditActions.UserTwoFactorDisabled));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(other.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMeAsync(acting.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(factor.AccountId, other.RefreshToken)).StatusCode);
        await AssertEverySessionAndAppEndedAsync(factor.AccountId, factor.UserId, freshRefreshRows: 1);
        Assert.Equal(HttpStatusCode.Unauthorized, await AppProbeAsync(app));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(fresh.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(factor.AccountId, fresh.RefreshToken)).StatusCode);
        // Login asks for no code.
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurningTwoFactorOff_WhenItIsNotOn_Is204_AndChangesNoSession(bool enrolmentStarted)
    {
        var email = $"tf-plain-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await UserIdAsync(accountId, email);
        var session = await factory.LoginAsync(email);
        if (enrolmentStarted) await StartEnrolmentAsync(accountId, userId);
        var client = factory.CreateAuthedClient(session.AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var refreshRows = await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens.CountAsync(t => t.UserId == userId));

        var response = await DeleteWithGrantAsync(client, TurnOffPath, grant!);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await TwoFactorRowsAsync(accountId, userId));
        Assert.Empty(await AuditRowsAsync(accountId, userId, AuditActions.UserTwoFactorDisabled));
        Assert.Equal(refreshRows, await factory.WithTenantScopeAsync(accountId, db => db.RefreshTokens.CountAsync(t => t.UserId == userId)));
        Assert.Equal(HttpStatusCode.OK, (await GetMeAsync(session.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(accountId, session.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task TurningTwoFactorOff_WithoutAGrant_Is403_AndChangesNothing()
    {
        var (_, factor, client) = await SignedInEnrolledAsync();

        var response = await DeleteWithGrantAsync(client, TurnOffPath, "not-a-grant");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1 + factor.RecoveryCodes.Count, (await TwoFactorRowsAsync(factor.AccountId, factor.UserId)).Count);
    }

    // The turn-off spends its grant before it takes the user-row lock; a
    // change that lands in between leaves the asking session stale.
    private async Task AssertStaleTurnOffRefusedAsync(HttpResponseMessage response, EnrolledFactor factor)
    {
        Assert.True(factory.BeforeCommand.Fired, "the concurrent change never ran");
        Assert.True((await FactorStateAsync(factor.AccountId, factor.UserId)).Enabled, "the factor was cleared");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", (await ProblemOf(response)).Title);
        Assert.Equal(1 + factor.RecoveryCodes.Count, (await TwoFactorRowsAsync(factor.AccountId, factor.UserId)).Count);
        Assert.Empty(await AuditRowsAsync(factor.AccountId, factor.UserId, AuditActions.UserTwoFactorDisabled));
    }

    [Theory]
    [InlineData("Owner password reset")]
    [InlineData("disable")]
    public async Task TurningTwoFactorOff_AfterTheSessionWentStaleUnderTheRequest_IsRefused(string change)
    {
        var (accountId, ownerId, _) = await FarmWithOwnerAsync();
        var (email, factor) = await EnrolledManagerAsync(accountId);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () =>
        {
            using var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, ownerId);
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            var done = change == "disable"
                ? await identity.DisableUserAsync(accountId, factor.UserId, ownerId, "test")
                : await identity.SetUserPasswordAsync(accountId, factor.UserId, NewPassword);
            Assert.True(done.IsSuccess, change);
        });

        var response = await DeleteWithGrantAsync(client, TurnOffPath, grant!);

        await AssertStaleTurnOffRefusedAsync(response, factor with { RecoveryCodes = factor.RecoveryCodes.Skip(1).ToList() });
    }

    // A password-only grant from before an enrolment must not remove the new
    // factor without its code.
    [Fact]
    public async Task TurningTwoFactorOff_WithAGrantFromBeforeAnEnrolment_IsRefused()
    {
        var email = $"tf-plain-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        EnrolledFactor? factor = null;
        factory.BeforeCommand.Arm("FOR NO KEY UPDATE", async () => factor = await factory.EnrolTwoFactorAsync(email));

        var response = await DeleteWithGrantAsync(client, TurnOffPath, grant!);

        Assert.NotNull(factor);
        await AssertStaleTurnOffRefusedAsync(response, factor);
    }
}
