namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #283 — server-side enforcement of the first-run "you must set a new
// password" gate (MustChangePasswordMiddleware). This is the API half of the
// guarantee: claims.ts's UI gate is voluntary, this is not — a caller that
// skips the SPA and hits the API directly gets the SAME refusal.
[Collection(IntegrationCollection.Name)]
public sealed class MustChangePasswordGateTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task PendingUser_IsBlockedFromOrdinaryEndpoints_With403()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);

        var token = await factory.LoginForAccessTokenAsync(email);
        var client = factory.CreateAuthedClient(token);

        var response = await client.GetAsync("/api/v1/flocks");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Auth.MustChangePassword", body);
    }

    // #339 review — UseExceptionHandler (Program.cs) re-executes the whole
    // downstream pipeline at /error when an allowed endpoint's request
    // throws (e.g. a malformed-JSON body on change-password, or — as forced
    // here, deterministically — the handler itself), so the gate must not
    // treat that internal replay as a fresh request to a disallowed path.
    // Before the fix this returned Auth.MustChangePassword 403 instead of
    // the /error endpoint's intended mapped response, hiding the real
    // failure. IIdentityProvider is swapped for a decorator that only
    // overrides ChangeOwnPasswordAsync (everything else — including the
    // login this test still needs — delegates to the real implementation),
    // so the throw is 100% deterministic rather than depending on a
    // malformed body actually reaching UseExceptionHandler as an unhandled
    // exception, which minimal-API body binding does not reliably do.
    [Fact]
    public async Task PendingUser_AllowedEndpointHandlerThrows_GetsMappedError_NotMustChangePassword403()
    {
        using var throwingFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<Cluckwork.Infrastructure.Identity.IdentityProvider>();
                services.AddScoped<IIdentityProvider>(sp =>
                    new ChangePasswordThrowingIdentityProvider(
                        sp.GetRequiredService<Cluckwork.Infrastructure.Identity.IdentityProvider>()));
            }));

        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);

        // Login through the SAME throwing factory (its decorator only
        // overrides ChangeOwnPasswordAsync, so login still behaves normally)
        // — it must share the app's JWT signing key and DB, which it does
        // since WithWebHostBuilder only layers ConfigureTestServices on top
        // of the existing host configuration.
        var loginClient = throwingFactory.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        loginResponse.EnsureSuccessStatusCode();
        var token = (await loginResponse.Content.ReadFromJsonAsync<Cluckwork.Api.Endpoints.Auth.AccessTokenResponse>())!.AccessToken;

        var client = throwingFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword = $"Aa1!{Guid.NewGuid():N}" });

        // Mapped by /error's catch-all (Program.cs) to a generic 500 Problem
        // — the fix's job is only to let this reach /error un-gated, not to
        // change what /error decides to return for an arbitrary exception
        // type. The status assertion is the real proof the gate stepped
        // aside: 403 (the pre-fix bug) or 400 "Idempotency-Key header is
        // required" (a related re-execution gap in IdempotencyMiddleware,
        // fixed alongside this) would both be silently-wrong outcomes here.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Auth.MustChangePassword", body);
        Assert.DoesNotContain("Idempotency-Key", body);
    }

    [Fact]
    public async Task PendingUser_CanStillReach_ChangePassword_And_Logout()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);

        var tokens = await factory.LoginAsync(email);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        var newPassword = $"Aa1!{Guid.NewGuid():N}";
        var changeResponse = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword });
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        // Logout must always be reachable too, even before any change (a
        // second pending user in the same class of scenario) — assert with a
        // freshly seeded second account so the first's already-revoked
        // session doesn't confuse the read.
        var secondAccountId = Guid.NewGuid();
        var secondEmail = $"pending2-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(secondAccountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(secondAccountId, "Gate Farm 2", "farm-" + secondAccountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(secondAccountId, secondEmail);
        var secondTokens = await factory.LoginAsync(secondEmail);
        var logoutResponse = await factory.CreateClient().PostLogoutAsync(
            secondTokens.RefreshToken, accessToken: secondTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
    }

    [Fact]
    public async Task PendingUser_CanReachTheTrailingSlashLogoutRoute()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Account.Create(accountId, "Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);
        var tokens = await factory.LoginAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", $"{AuthCookies.RefreshCookieNameFor(accountId)}={tokens.RefreshToken}");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountId.ToString());

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ChangingPassword_ClearsTheFlag_AndUnblocksOrdinaryEndpoints()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "farm-" + accountId.ToString("N")[..12], "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(accountId, email);

        var loginTokens = await factory.LoginAsync(email);
        var loginClient = factory.CreateAuthedClient(loginTokens.AccessToken);

        var newPassword = $"Aa1!{Guid.NewGuid():N}";
        var changeResponse = await loginClient.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword });
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        var newAccessToken = (await changeResponse.Content
            .ReadFromJsonAsync<Cluckwork.Api.Endpoints.Auth.AccessTokenResponse>())!.AccessToken;

        var freshClient = factory.CreateAuthedClient(newAccessToken);
        var flocksResponse = await freshClient.GetAsync("/api/v1/flocks");
        Assert.Equal(HttpStatusCode.OK, flocksResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillPending = await db.Users.Where(u => u.Email == email)
            .Select(u => u.MustChangePassword).SingleAsync();
        Assert.False(stillPending);
    }
}

// Delegates every IIdentityProvider call to the real implementation except
// ChangeOwnPasswordAsync, which throws — a deterministic way to make the
// change-password endpoint's HANDLER fail (as opposed to a malformed
// request body) so the /error re-execution path above is exercised
// reliably, without depending on framework body-binding exception behavior.
internal sealed class ChangePasswordThrowingIdentityProvider(IIdentityProvider inner) : IIdentityProvider
{
    public Task<Result<TokenPair>> LoginAsync(
        Guid accountId, string email, string password, CancellationToken ct = default) =>
        inner.LoginAsync(accountId, email, password, ct);

    public Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null) =>
        inner.RefreshAsync(refreshToken, ct, expectedAccountId);

    public Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default) =>
        inner.RevokeRefreshTokenAsync(refreshToken, ct);

    public Task RecordLogoutAsync(Guid userId, CancellationToken ct = default) =>
        inner.RecordLogoutAsync(userId, ct);

    public Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role,
        string? name = null, bool mustChangePassword = false, CancellationToken ct = default) =>
        inner.CreateUserAsync(accountId, email, password, role, name, mustChangePassword, ct);

    public Task<Result> UpdateUserAsync(Guid accountId, Guid userId, string? name, CancellationToken ct = default) =>
        inner.UpdateUserAsync(accountId, userId, name, ct);

    public Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default) =>
        inner.SetUserPasswordAsync(accountId, userId, newPassword, ct);

    public Task<Result> ChangeUserRoleAsync(
        Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default) =>
        inner.ChangeUserRoleAsync(accountId, userId, role, actingUserId, ct);

    public Task<Result> DisableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, string? reason, CancellationToken ct = default) =>
        inner.DisableUserAsync(accountId, userId, actingUserId, reason, ct);

    public Task<Result> EnableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        inner.EnableUserAsync(accountId, userId, actingUserId, ct);

    public Task<Result> BreakGlassResetAsync(
        Guid accountId, Guid userId, string newPassword, string? reason, CancellationToken ct = default) =>
        inner.BreakGlassResetAsync(accountId, userId, newPassword, reason, ct);

    public Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "Simulated failure (MustChangePasswordGateTests) — verifies the /error re-execution path is not gated.");

    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default) =>
        inner.ListUsersAsync(accountId, ct);

    public Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
        inner.GetUserAsync(accountId, userId, ct);

    public Task<Result> SetLanguageAsync(Guid accountId, Guid userId, string? language, CancellationToken ct = default) =>
        inner.SetLanguageAsync(accountId, userId, language, ct);

    public Task<Result> SetStepperUnitAsync(Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default) =>
        inner.SetStepperUnitAsync(accountId, userId, unit, ct);
}
