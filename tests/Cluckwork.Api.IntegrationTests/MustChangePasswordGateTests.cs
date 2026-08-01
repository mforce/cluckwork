namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
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
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "UTC", "USD"));
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

    [Fact]
    public async Task PendingUser_CanStillReach_ChangePassword_And_Logout()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "UTC", "USD"));
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
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(secondAccountId, "Gate Farm 2", "UTC", "USD"));
            await db.SaveChangesAsync();
        });
        await factory.SeedUserPendingPasswordChangeAsync(secondAccountId, secondEmail);
        var secondTokens = await factory.LoginAsync(secondEmail);
        var anonClient = factory.CreateClient();
        var logoutResponse = await anonClient.PostLogoutAsync(secondTokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
    }

    [Fact]
    public async Task ChangingPassword_ClearsTheFlag_AndUnblocksOrdinaryEndpoints()
    {
        var accountId = Guid.NewGuid();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Accounts.Add(Cluckwork.Domain.Accounts.Account.Create(accountId, "Gate Farm", "UTC", "USD"));
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
