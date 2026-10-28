using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #1278: the login code step for an enrolled user (#1274 "What a caller sees").
[Collection(IntegrationCollection.Name)]
public sealed class SecondFactorLoginTests(CluckworkWebApplicationFactory factory)
{
    private async Task<(string Email, EnrolledFactor Factor)> SeedEnrolledAsync()
    {
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return (email, await factory.EnrolTwoFactorAsync(email));
    }

    private Task<UserCounters> CountersAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.AsNoTracking()
            .Where(u => u.Id == factor.UserId)
            .Select(u => new UserCounters(u.AccessFailedCount, u.SecondFactorFailures, u.SecondFactorSignIns, u.LockoutEnd))
            .SingleAsync());

    private Task<int> RefreshRowsAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.RefreshTokens.CountAsync(t => t.UserId == factor.UserId));

    private sealed record UserCounters(int AccessFailed, int DailyFailures, long SignIns, DateTimeOffset? LockoutEnd);

    [Fact]
    public async Task AnEnrolledUser_SignsInWithTheirAuthenticatorCode()
    {
        var (email, factor) = await SeedEnrolledAsync();

        var response = await factory.LoginWithSecondFactorAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await TestHarness.ReadTokensAsync(response);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        Assert.Equal(1, (await CountersAsync(factor)).SignIns);
    }
}
