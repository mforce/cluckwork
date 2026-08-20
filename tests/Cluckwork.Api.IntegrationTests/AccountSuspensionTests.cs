namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Application.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #532 — AccountSuspensionService. The service has NO caller in this slice (#534
// ships the operator verbs), so these tests are the only thing that exercises it
// and they invoke it directly out of DI.
//
// The guarantee worth testing is not "IsActive goes false" — a boolean-only
// implementation (IsActive flipped, no epoch bump, no revocation) passes most of
// this file, and the tests that pin each specific guarantee are:
//   * the middleware GATE (the AccountIsActive clause in the per-request read)
//     → ABearerWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive
//   * the suspended-farm check in RefreshAsync
//     → ARefreshTokenWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive
//   * reactivation's revoke (nothing survives a suspend/reactivate cycle)
//     → ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate
//   * account scoping (the ExecuteUpdateAsync pair is scoped by AccountId and
//     does not sweep the whole users table)
//     → SuspendAsync_LeavesAnotherFarmUntouched
// Round-3 review proved the first three guards were ALL simultaneously
// deletable with this suite green: every test suspended through
// AccountSuspensionService, which also bumps the epoch, so the epoch clause
// rejected the request and the account clause never had to work.
// DeactivateWithoutEpochBumpAsync below is the fixture that breaks that
// coupling.
[Collection(IntegrationCollection.Name)]
public sealed class AccountSuspensionTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserState(int CredentialEpoch, string? SecurityStamp, string? ConcurrencyStamp);

    // One scope per call: AccountSuspensionService resolves the tenant itself and
    // TenantContext is single-assignment (#546), so a shared scope throws on the
    // second call.
    private async Task SuspendAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.SuspendAsync(accountId);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");
    }

    private async Task ReactivateAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.ReactivateAsync(accountId);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : "");
    }

    private async Task<UserState> ReadUserAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        return new UserState(user.CredentialEpoch, user.SecurityStamp, user.ConcurrencyStamp);
    }

    private async Task<int> LiveRefreshTokenCountAsync(Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.AccountId == accountId && t.RevokedAt == null);
    }

    // Flips IsActive WITHOUT touching CredentialEpoch. Every other test in this
    // file suspends through AccountSuspensionService, which also bumps the epoch —
    // so the epoch clause rejects the request and the account clause never has to
    // work. That coupling is why three guards were simultaneously deletable with
    // this suite green (round-3 review). This is the state a bearer minted in the
    // same instant as the suspension commit would face.
    private Task DeactivateWithoutEpochBumpAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.Where(a => a.Id == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsActive, false)));

    private Task<bool> IsActiveAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db =>
            db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.IsActive).SingleAsync());

    private async Task<HttpResponseMessage> TryLoginAsync(string farmCode, string email)
    {
        var client = factory.CreateClient();
        return await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { farmCode, email, password = TestHarness.Password });
    }

    [Fact]
    public async Task SuspendAsync_BumpsEveryEpoch_RotatesBothStamps_AndRevokesRefreshTokens()
    {
        var owner = $"susp-owner-{Guid.NewGuid():N}@test.local";
        var worker = $"susp-worker-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        await factory.SeedUserAsync(accountId, worker, asAdmin: false);

        // Two live sessions, so the revocation assertion below covers more than
        // the single row a one-user farm would produce.
        _ = await factory.LoginAsync(owner);
        _ = await factory.LoginAsync(worker);
        Assert.Equal(2, await LiveRefreshTokenCountAsync(accountId));

        var ownerBefore = await ReadUserAsync(owner);
        var workerBefore = await ReadUserAsync(worker);

        await SuspendAsync(accountId);

        Assert.False(await IsActiveAsync(accountId));

        var ownerAfter = await ReadUserAsync(owner);
        var workerAfter = await ReadUserAsync(worker);

        // Epoch +1 exactly — not merely "changed". An implementation that reset it
        // to a constant would kill today's tokens and silently unkill tomorrow's.
        Assert.Equal(ownerBefore.CredentialEpoch + 1, ownerAfter.CredentialEpoch);
        Assert.Equal(workerBefore.CredentialEpoch + 1, workerAfter.CredentialEpoch);

        // SecurityStamp kills outstanding STEP-UP grants, which bind to the stamp
        // and never to CredentialEpoch.
        Assert.NotEqual(ownerBefore.SecurityStamp, ownerAfter.SecurityStamp);
        Assert.NotEqual(workerBefore.SecurityStamp, workerAfter.SecurityStamp);

        // ConcurrencyStamp is the fence: without rotating it, a concurrent
        // same-user Identity write that read the row BEFORE this transaction still
        // matches on UpdateAsync and writes its stale CredentialEpoch back.
        Assert.NotEqual(ownerBefore.ConcurrencyStamp, ownerAfter.ConcurrencyStamp);
        Assert.NotEqual(workerBefore.ConcurrencyStamp, workerAfter.ConcurrencyStamp);

        Assert.Equal(0, await LiveRefreshTokenCountAsync(accountId));
    }

    [Fact]
    public async Task SuspendedFarm_RejectsLogin_WithFarmSuspended()
    {
        var email = $"susp-login-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmCode = await factory.FarmCodeForAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(farmCode, email)).StatusCode);

        await SuspendAsync(accountId);

        var response = await TryLoginAsync(farmCode, email);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    [Fact]
    public async Task SuspendedFarm_KillsAnInFlightBearer_OnTheNextRequest()
    {
        var email = $"susp-bearer-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        // Minted BEFORE the suspension and still well inside its 15-minute
        // lifetime: this is the bypass the whole slice exists to close.
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        await SuspendAsync(accountId);

        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Auth.FarmSuspended, NOT Auth.CredentialsSuperseded. Suspension bumps the
        // epoch too, so this bearer fails both tests; asserting the title is what
        // pins the middleware's precedence (suspended farm BEFORE epoch). Telling
        // someone to "sign in again" when their sign-in cannot succeed is the bug.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    [Fact]
    public async Task ReactivateAsync_BringsTheFarmBack_ButPreSuspensionSessionsStayDead()
    {
        var email = $"susp-cycle-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmCode = await factory.FarmCodeForAsync(email);

        var tokens = await factory.LoginAsync(email);
        var staleClient = factory.CreateAuthedClient(tokens.AccessToken);

        await SuspendAsync(accountId);
        await ReactivateAsync(accountId);

        Assert.True(await IsActiveAsync(accountId));

        // The farm works again.
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(farmCode, email)).StatusCode);

        // But nothing minted before the suspension survives the cycle. This is the
        // assertion a boolean-only implementation fails: with IsActive back to
        // true and no epoch bump / no revocation, both of these would succeed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await staleClient.GetAsync("/api/v1/users")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            Assert.True((await identity.RefreshAsync(tokens.RefreshToken)).IsFailure,
                "a refresh token minted before the suspension must not survive reactivation");
        }
    }

    [Fact]
    public async Task SuspendAsync_LeavesAnotherFarmUntouched()
    {
        var victimEmail = $"susp-a-{Guid.NewGuid():N}@test.local";
        var bystanderEmail = $"susp-b-{Guid.NewGuid():N}@test.local";
        var victimAccountId = await factory.SeedAccountWithUserAsync(victimEmail);
        var bystanderAccountId = await factory.SeedAccountWithUserAsync(bystanderEmail);
        var bystanderFarmCode = await factory.FarmCodeForAsync(bystanderEmail);

        _ = await factory.LoginAsync(victimEmail);
        var bystanderTokens = await factory.LoginAsync(bystanderEmail);
        var bystanderClient = factory.CreateAuthedClient(bystanderTokens.AccessToken);
        var bystanderBefore = await ReadUserAsync(bystanderEmail);

        await SuspendAsync(victimAccountId);

        // Both ExecuteUpdateAsync statements are scoped by AccountId. Drop either
        // WHERE clause and this test goes red while every other test in the file
        // still passes.
        Assert.True(await IsActiveAsync(bystanderAccountId));
        var bystanderAfter = await ReadUserAsync(bystanderEmail);
        Assert.Equal(bystanderBefore.CredentialEpoch, bystanderAfter.CredentialEpoch);
        Assert.Equal(bystanderBefore.SecurityStamp, bystanderAfter.SecurityStamp);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(bystanderAccountId));
        Assert.Equal(HttpStatusCode.OK, (await bystanderClient.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(bystanderFarmCode, bystanderEmail)).StatusCode);
    }

    [Fact]
    public async Task ABearerWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive()
    {
        var email = $"susp-gate-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        await DeactivateWithoutEpochBumpAsync(accountId);

        // The epoch still matches, so the epoch clause cannot reject this. Only
        // the AccountIsActive clause in the GATE can. Delete it and this is a 200.
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, problem!.Title);
    }

    [Fact]
    public async Task ARefreshTokenWhoseEpochStillMatches_IsRejected_WhenTheFarmIsInactive()
    {
        var email = $"susp-refresh-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        await DeactivateWithoutEpochBumpAsync(accountId);

        // IssuedEpoch still equals the user's CredentialEpoch, so the epoch check
        // in RefreshAsync passes. Only the suspended-farm check rejects this.
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        Assert.True((await identity.RefreshAsync(tokens.RefreshToken)).IsFailure,
            "a suspended farm must not rotate a session, even when the epoch still matches");
    }

    [Fact]
    public async Task ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate()
    {
        var email = $"susp-reactivate-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await SuspendAsync(accountId);

        // A refresh row that post-dates the suspension sweep — the exact artifact
        // the login/suspend race produces. Inserted directly because the race is
        // not reproducible on demand; what matters is that reactivation kills it.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            var epoch = await db.Users.Where(u => u.Id == userId).Select(u => u.CredentialEpoch).SingleAsync();
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AccountId = accountId,
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IssuedEpoch = epoch,
            });
            await db.SaveChangesAsync();
        });
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));

        await ReactivateAsync(accountId);

        // Set revokeSessions to false on ReactivateAsync and this is the ONLY
        // test that reddens. The pre-suspension-session test does not: suspend
        // already bumped the epoch, so those credentials are dead either way.
        Assert.Equal(0, await LiveRefreshTokenCountAsync(accountId));
    }

    [Fact]
    public async Task ReactivatingAnAlreadyActiveFarm_DoesNotSignAnybodyOut()
    {
        var email = $"susp-noop-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);
        var before = await ReadUserAsync(email);

        // #534's reactivate verb is the kind of command an operator retries.
        await ReactivateAsync(accountId);

        var after = await ReadUserAsync(email);
        Assert.Equal(before.CredentialEpoch, after.CredentialEpoch);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(1, await LiveRefreshTokenCountAsync(accountId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task ADisabledUserInASuspendedFarm_IsToldTheirAccountIsDisabled()
    {
        var owner = $"susp-prec-owner-{Guid.NewGuid():N}@test.local";
        var victim = $"susp-prec-victim-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(owner);
        await factory.SeedUserAsync(accountId, victim, asAdmin: false);
        var tokens = await factory.LoginAsync(victim);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        // The middleware reads DisabledAt from the USER row, never from the JWT
        // claims, so this must be persisted — and persisted BEFORE the suspension
        // bumps the epoch, or the bearer would fail the epoch clause first and the
        // precedence under test would never be exercised.

        await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == victim)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));
        await SuspendAsync(accountId);

        // Both conditions are true at once. Precedence says DISABLED wins: the
        // farm's suspension is not this person's actionable problem. Swap the two
        // clauses in CredentialEpochMiddleware and only this test reddens.
        var response = await client.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Auth.AccountDisabled", problem!.Title);
    }

    [Fact]
    public async Task SuspendAsync_ForAnUnknownAccount_ReturnsNotFound()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountSuspensionService>();
        var result = await service.SuspendAsync(Guid.NewGuid());
        Assert.True(result.IsFailure);
    }
}
