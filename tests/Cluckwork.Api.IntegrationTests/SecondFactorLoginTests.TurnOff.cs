using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
}
