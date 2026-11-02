using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.IntegrationTests;

// #1278 G3.31: rotation slides ExpiresAt, so an enrolled lineage also ends
// Auth:EnrolledSessionMaxDays after its last full sign-in (#1274 decision 8).
[Collection(SecondFactorCollection.Name)]
public sealed class EnrolledSessionCapTests(SecondFactorFactory factory) : IDisposable
{
    public void Dispose() => factory.Clock.Reset();

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    // Refreshes once a day for `days` days; returns each answer's status and
    // the last cookie the server rotated to.
    private async Task<(List<HttpStatusCode> Answers, string Latest)> RefreshDailyAsync(
        string refreshToken, Guid accountId, int days)
    {
        var answers = new List<HttpStatusCode>();
        for (var i = 0; i < days; i++)
        {
            factory.Clock.Advance(Day);
            var response = await RefreshAsync(refreshToken, accountId);
            answers.Add(response.StatusCode);
            if (response.IsSuccessStatusCode) refreshToken = TestHarness.ExtractRefreshCookie(response, accountId);
        }
        return (answers, refreshToken);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken, Guid accountId) =>
        factory.CreateClient(TestHarness.Cookieless(factory)).PostRefreshAsync(refreshToken, expectedAccount: accountId.ToString());

    [Fact]
    public async Task AnEnrolledSession_ThatRefreshesDaily_EndsThirtyDaysAfterTheCodeStep()
    {
        var email = $"cap-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await factory.EnrolTwoFactorAsync(email);
        var tokens = await factory.LoginAsync(email);

        var (answers, latest) = await RefreshDailyAsync(tokens.RefreshToken, accountId, 29);
        Assert.All(answers, status => Assert.Equal(HttpStatusCode.OK, status));

        factory.Clock.Advance(Day - TimeSpan.FromMinutes(1));
        var lastInside = await RefreshAsync(latest, accountId);
        Assert.Equal(HttpStatusCode.OK, lastInside.StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var refused = await RefreshAsync(TestHarness.ExtractRefreshCookie(lastInside, accountId), accountId);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Identity.ExpiredRefreshToken", (await TestHarness.ReadProblemAsync(refused)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task AnUnenrolledSession_RefreshesPastThirtyDays()
    {
        var ownerEmail = $"cap-owner-{Guid.NewGuid():N}@test.local";
        var email = $"cap-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        await factory.SeedUserAsync(accountId, email, Roles.Manager);
        var tokens = await factory.LoginAsync(email);

        Assert.All((await RefreshDailyAsync(tokens.RefreshToken, accountId, 31)).Answers, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    [Fact]
    public async Task AConnectedAppOfAnEnrolledUser_IsOutsideTheCap()
    {
        var email = $"cap-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        await factory.EnrolTwoFactorAsync(email);
        var app = await ConnectedAppToken.IssueAsync(factory, (await factory.LoginAsync(email)).AccessToken);

        factory.Clock.Advance(TimeSpan.FromDays(31));
        using var probe = await ConnectedAppToken.Client(factory, app).GetAsync(SecondFactorFactory.OAuthProbePath);

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("366")]
    [InlineData("1.5")]
    public void AnOutOfRangeOrFractionalCap_FailsTheBoot(string days)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:EnrolledSessionMaxDays", days));
        Assert.ThrowsAny<Exception>(() => host.CreateClient());
    }

    [Fact]
    public void AnUnsetCap_IsThirtyDays() =>
        Assert.Equal(30, factory.Services.GetRequiredService<IOptions<EnrolledSessionOptions>>().Value.EnrolledSessionMaxDays);
}
