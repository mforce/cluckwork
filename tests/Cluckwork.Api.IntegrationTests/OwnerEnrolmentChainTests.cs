using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// bootstrap-admin needs a default farm with no Owner, so the class has its own database.
public sealed class OwnerEnrolmentChainFactory : CluckworkWebApplicationFactory;

// #1281 G6.3: an Owner from bootstrap-admin or provision-account signs in with
// the generated password, changes it, is sent to enrolment once the grace has
// passed, enrols through the API and then reaches the app (#1274 decision 18).
public sealed class OwnerEnrolmentChainTests(OwnerEnrolmentChainFactory factory)
    : IClassFixture<OwnerEnrolmentChainFactory>
{
    private const string GatedPath = "/api/v1/users";

    [Theory]
    [InlineData("bootstrap-admin")]
    [InlineData("provision-account")]
    public async Task AProvisionedOwner_ChangesThePassword_EnrolsAndThenReachesTheApp(string verb)
    {
        var (accountId, slug, email, temporaryPassword) = await ProvisionAsync(verb);

        var firstLogin = await PostAsync(null, "/api/v1/auth/login", new { farmCode = slug, email, password = temporaryPassword });
        var firstToken = await AccessTokenAsync(firstLogin);
        var passwordGate = await GetAsync(firstToken);
        var newPassword = $"Aa1!{Guid.NewGuid():N}";
        var changed = await AccessTokenAsync(await PostAsync(firstToken, "/api/v1/auth/change-password",
            new { currentPassword = temporaryPassword, newPassword }));
        await PastTheDeadlineAsync(accountId, email);
        var enrolmentGate = await GetAsync(changed);

        var grant = (await JsonAsync(await PostAsync(changed, "/api/v1/auth/step-up", new { password = newPassword }))).GetProperty("token").GetString()!;
        var start = await JsonAsync(await PostAsync(changed, "/api/v1/auth/two-factor/start", body: null, grant));
        var code = TotpMatcher.Compute(TotpMatcher.DecodeBase32(start.GetProperty("key").GetString()!),
            TotpMatcher.StepAt(factory.Services.GetRequiredService<TimeProvider>().GetUtcNow())).ToString("D6");
        var enrolled = await AccessTokenAsync(await PostAsync(changed, "/api/v1/auth/two-factor/confirm", new { code }));
        var reached = await GetAsync(enrolled);

        Assert.Equal((HttpStatusCode.Forbidden, "Auth.MustChangePassword"), passwordGate);
        Assert.Equal((HttpStatusCode.Forbidden, EnrolmentErrorCodes.EnrolmentRequired), enrolmentGate);
        Assert.Equal((HttpStatusCode.OK, null), reached);
    }

    private async Task<(Guid AccountId, string Slug, string Email, string Password)> ProvisionAsync(string verb)
    {
        using var scope = factory.Services.CreateScope();
        if (verb == "bootstrap-admin")
        {
            var email = $"chain-first-{Guid.NewGuid():N}@example.test";
            var first = await scope.ServiceProvider.GetRequiredService<FirstRunAdminService>().ProvisionAsync(email);
            Assert.True(first.IsSuccess && !first.Value.WasAlreadyProvisioned, "bootstrap-admin created no Owner");
            return (first.Value.AccountId!.Value, first.Value.Slug!, email, first.Value.TemporaryPassword!);
        }

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var farm = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
            .ProvisionAsync("Chain Farm", $"chain-{suffix}", $"chain-{suffix}@example.test", "en-US", "usd");
        Assert.True(farm.IsSuccess, farm.IsFailure ? farm.Error.Description : null);
        return (farm.Value.AccountId, farm.Value.Slug, farm.Value.OwnerEmail, farm.Value.TemporaryPassword);
    }

    // The grace began, at the first request, more than OwnerGraceDays ago.
    private Task PastTheDeadlineAsync(Guid accountId, string email) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users.Where(u => u.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorRequiredSince, DateTimeOffset.UtcNow.AddDays(-15))));

    private async Task<HttpResponseMessage> PostAsync(string? bearer, string path, object? body, string? grant = null)
    {
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (grant is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        return await client.SendAsync(request);
    }

    private async Task<(HttpStatusCode, string?)> GetAsync(string bearer)
    {
        using var response = await factory.CreateAuthedClient(bearer).GetAsync(GatedPath);
        return (response.StatusCode, response.IsSuccessStatusCode ? null
            : (await TestHarness.ReadProblemAsync(response)).GetProperty("title").GetString());
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage!.RequestUri} answered {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> AccessTokenAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("accessToken").GetString()!;
}
