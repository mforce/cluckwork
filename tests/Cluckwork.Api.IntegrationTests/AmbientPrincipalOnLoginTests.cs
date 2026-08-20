namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Middleware;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #532 — /auth/login must behave IDENTICALLY whether or not the caller happens
// to send a bearer. AllowAnonymous does not deliver that: UseAuthentication
// still populates context.User, and three middlewares downstream read it.
//
// The case with teeth is a FULLY VALID bearer for another farm. It passes every
// middleware, so it is the only one that reaches the handler — and before
// AmbientPrincipalMiddleware it resolved the wrong tenant, made
// AccessFailedAsync write across the tenant boundary, threw
// TenantWriteMismatchException (caught nowhere in src/), and returned 500 with
// AccessFailedCount UNCHANGED. That is an unlimited-guessing bypass of the #128
// account lockout against any other farm's users, so the counter assertion
// below is the point of this file, not the status code.
[Collection(IntegrationCollection.Name)]
public sealed class AmbientPrincipalOnLoginTests(CluckworkWebApplicationFactory factory)
{
    private static HttpClient WithBearer(CluckworkWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<int> AccessFailedCountAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users
            .Where(u => u.Email == email)
            .Select(u => u.AccessFailedCount)
            .SingleAsync());

    [Fact]
    public async Task AForeignFarmsValidBearer_DoesNotSuppressTheVictimsLockoutCounter()
    {
        var attackerEmail = $"amb-attacker-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(attackerEmail);
        var victimEmail = $"amb-victim-{Guid.NewGuid():N}@test.local";
        var victimAccountId = await factory.SeedAccountWithUserAsync(victimEmail);

        var attackerToken = (await factory.LoginAsync(attackerEmail)).AccessToken;
        var victimFarmCode = await factory.FarmCodeForAsync(victimEmail);
        var before = await AccessFailedCountAsync(victimAccountId, victimEmail);

        var response = await WithBearer(factory, attackerToken).PostAsJsonAsync(
            "/api/v1/auth/login",
            new { farmCode = victimFarmCode, email = victimEmail, password = "WrongPassw0rd!x" });

        // A wrong password is a 401, not a cross-tenant write failure.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // THE assertion: the attempt was charged to the victim. If this reads
        // `before`, the lockout has been bypassed and the guessing is unbounded.
        Assert.Equal(before + 1, await AccessFailedCountAsync(victimAccountId, victimEmail));
    }

    [Fact]
    public async Task AForeignFarmsValidBearer_DoesNotBlockAnOtherwiseGoodLogin()
    {
        var attackerEmail = $"amb-other-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(attackerEmail);
        var targetEmail = $"amb-target-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(targetEmail);

        var attackerToken = (await factory.LoginAsync(attackerEmail)).AccessToken;

        var response = await WithBearer(factory, attackerToken).PostAsJsonAsync(
            "/api/v1/auth/login",
            new
            {
                farmCode = await factory.FarmCodeForAsync(targetEmail),
                email = targetEmail,
                password = TestHarness.Password,
            });

        // Identical to the no-bearer outcome. Asserting SUCCESS, not merely
        // "not 401/403": a swap that 500s would satisfy a negative assertion.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AGarbageBearer_DoesNotBlockAGoodLogin()
    {
        var email = $"amb-garbage-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var response = await WithBearer(factory, "not.a.valid.token").PostAsJsonAsync(
            "/api/v1/auth/login",
            new
            {
                farmCode = await factory.FarmCodeForAsync(email),
                email,
                password = TestHarness.Password,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ASupersededBearer_DoesNotBlockAGoodLogin()
    {
        var email = $"amb-superseded-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var token = (await factory.LoginAsync(email)).AccessToken;

        // Supersede the credential the bearer was minted under. Without the
        // principal blanking, CredentialEpochMiddleware rejects this request
        // with Auth.CredentialsSuperseded BEFORE the handler ever reads the
        // farm code — so a stale tab would break a fresh sign-in.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            user.CredentialEpoch++;
            await db.SaveChangesAsync();
        });

        var response = await WithBearer(factory, token).PostAsJsonAsync(
            "/api/v1/auth/login",
            new
            {
                farmCode = await factory.FarmCodeForAsync(email),
                email,
                password = TestHarness.Password,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // #532 round 3 — the SAME cross-farm case as the login tests above, but for
    // /auth/refresh: farm A's VALID bearer and farm B's VALID refresh cookie in
    // one request. Without /refresh's IgnoresAmbientPrincipalAttribute the
    // bearer resolves farm A as the ambient tenant while RefreshAsync rotates
    // farm B's cookie — a tracked SaveChanges that TenantStampInterceptor
    // refuses, so the caller gets a 500 instead of a clean rotation. Deleting
    // the marker reddens ONLY this file (nothing else in the suite sends a
    // bearer to /auth/refresh).
    [Fact]
    public async Task AForeignFarmsValidBearer_DoesNotBlockAnotherFarmsRefresh()
    {
        var bearerEmail = $"ref-a-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(bearerEmail);
        var cookieEmail = $"ref-b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(cookieEmail);

        var bearerToken = (await factory.LoginAsync(bearerEmail)).AccessToken;
        var cookieTokens = await factory.LoginAsync(cookieEmail);

        // A cookie-less client carrying the bearer by hand, so the bearer and
        // the cookie can name different farms in the same request.
        var client = WithBearer(factory, bearerToken);
        var response = await client.PostRefreshAsync(cookieTokens.RefreshToken);

        // Identical to the no-bearer outcome: a clean rotation. Asserting
        // SUCCESS, not merely "not 500": a swap that 401s the cookie owner's
        // own rotation would satisfy a negative assertion.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(string.Empty, TestHarness.ExtractRefreshCookie(response));
    }

    // #532 round 3 — the belt that pairs the braces above: both auth paths are
    // on IdempotencyMiddleware.ResponseNotCacheable, which is what keeps a live
    // access token out of idempotency_records if either endpoint ever resolves
    // a tenant again. Deleting EITHER entry reddens this test.
    [Fact]
    public void LoginAndRefresh_AreBothOnTheIdempotencyResponseNotCacheableList()
    {
        var field = typeof(IdempotencyMiddleware).GetField(
            "ResponseNotCacheable",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) as string[];

        Assert.NotNull(field);
        Assert.Contains("/api/v1/auth/login", field);
        Assert.Contains("/api/v1/auth/refresh", field);
    }
}
