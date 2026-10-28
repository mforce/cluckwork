using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cluckwork.Api.IntegrationTests;

// #1278: the login code step for an enrolled user (#1274 "What a caller sees").
[Collection(SecondFactorCollection.Name)]
public sealed partial class SecondFactorLoginTests(SecondFactorFactory factory) : IDisposable
{
    public void Dispose()
    {
        factory.Clock.Reset();
        factory.CommitFault.Disarm();
    }

    private sealed record UserCounters(int AccessFailed, int DailyFailures, long SignIns, long Watermark, DateTimeOffset? LockoutEnd);

    private async Task<(string Email, EnrolledFactor Factor)> SeedEnrolledAsync()
    {
        var email = $"tf-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return (email, await factory.EnrolTwoFactorAsync(email));
    }

    private Task<UserCounters> CountersAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.Users.AsNoTracking()
            .Where(u => u.Id == factor.UserId)
            .Select(u => new UserCounters(u.AccessFailedCount, u.SecondFactorFailures, u.SecondFactorSignIns,
                u.SecondFactorWatermark, u.LockoutEnd))
            .SingleAsync());

    private Task<int> RefreshRowsAsync(EnrolledFactor factor) =>
        factory.WithTenantScopeAsync(factor.AccountId, db => db.RefreshTokens.CountAsync(t => t.UserId == factor.UserId));

    private string CodeAt(EnrolledFactor factor, int stepOffset) =>
        TotpMatcher.Compute(TotpMatcher.DecodeBase32(factor.Key), TotpMatcher.StepAt(factory.Clock.GetUtcNow()) + stepOffset)
            .ToString("D6");

    // Six digits that match no step from -3 to +3, so they are a wrong guess
    // rather than a reuse.
    private string WrongCode(EnrolledFactor factor)
    {
        var near = Enumerable.Range(-3, 7).Select(offset => CodeAt(factor, offset)).ToHashSet();
        string code;
        do code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        while (near.Contains(code));
        return code;
    }

    private static async Task<(HttpStatusCode Status, string? Title, string? Detail)> ProblemOf(HttpResponseMessage response)
    {
        var body = await TestHarness.ReadProblemAsync(response);
        return (response.StatusCode, body.GetProperty("title").GetString(), body.GetProperty("detail").GetString());
    }

    private Task<HttpResponseMessage> PostCodeAsync(string challenge, string code) =>
        factory.PostSecondFactorAsync(challenge, code);

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

    // G3.1
    [Fact]
    public async Task ACorrectPassword_ForAnEnrolledUser_MintsNothing_AndIsNotAFailedLogin()
    {
        var (email, factor) = await SeedEnrolledAsync();
        factory.Sink.Events.Clear();

        var response = await factory.TryLoginAsync(email, TestHarness.Password);

        var pending = await TestHarness.ReadPendingLoginAsync(response);
        Assert.False(response.Headers.Contains("Set-Cookie"), "the pending answer set a cookie");
        var body = await TestHarness.ReadProblemAsync(response);
        Assert.False(body.TryGetProperty("accessToken", out _), "the pending answer carried an access token");
        Assert.NotEmpty(pending.Challenge);
        Assert.Equal(0, await RefreshRowsAsync(factor));
        Assert.Empty(factory.EventsFor(SecurityEvents.LoginFailed));
    }

    // G3.2: three barriers, each with its own case.
    [Fact]
    public async Task TheChallenge_IsRefusedAsABearerOnEveryAuthorizedRoute()
    {
        var (email, _) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pending.Challenge);

        var routes = AuthorizedRoutes();
        Assert.True(routes.Count >= 50, $"the walk found {routes.Count} authorized routes, below its floor of 50");
        var admitted = new List<string>();
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = JsonContent.Create(new { });
            using var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Unauthorized) admitted.Add($"{method} {path}: {(int)response.StatusCode}");
        }
        Assert.True(admitted.Count == 0, "the challenge was not refused as a bearer:\n" + string.Join("\n", admitted));
    }

    [Fact]
    public async Task TheChallenge_IsRefusedAsARefreshCookie_AndAsAStepUpGrant()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);

        var refresh = await factory.CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(pending.Challenge, expectedAccount: factor.AccountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        using var scope = factory.Services.CreateScope();
        var grant = await scope.ServiceProvider.GetRequiredService<IStepUpGrantService>()
            .ValidateAsync(factor.AccountId, factor.UserId, pending.Challenge);
        Assert.Equal(StepUpErrorCodes.Required, grant.Error.Code);
    }

    // The bearer handler refuses the audience: a token with the challenge's
    // audience and a valid credential_epoch still gets 401.
    [Fact]
    public async Task ATokenWithTheChallengeAudience_IsRefusedByTheBearerHandler()
    {
        var (email, _) = await SeedEnrolledAsync();
        var claims = ClaimsOf((await factory.PasswordStepAsync(email)).Challenge);
        claims.Add(new Claim("credential_epoch", claims.Single(c => c.Type == SecondFactorChallenge.EpochClaim).Value));

        var response = await GetMeAsync(Sign(claims, SecondFactorChallenge.Audience));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // CredentialEpochMiddleware refuses the claim set: the challenge's own
    // claims re-signed for the API audience carry no credential_epoch.
    [Fact]
    public async Task TheChallengeClaims_UnderTheApiAudience_AreRefusedByTheEpochCheck()
    {
        var (email, _) = await SeedEnrolledAsync();
        var claims = ClaimsOf((await factory.PasswordStepAsync(email)).Challenge);

        var response = await GetMeAsync(Sign(claims, factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value.Audience));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded", (await ProblemOf(response)).Title);
    }

    // G3.7
    [Fact]
    public async Task AnUnenrolledLogin_IsUnchanged_AndAnEnrolledSessionCarriesTheTwoFactorClaim()
    {
        var email = $"plain-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var plain = await factory.TryLoginAsync(email, TestHarness.Password);
        var body = JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["accessToken", "accessTokenExpiry"], body.EnumerateObject().Select(p => p.Name).Order());
        var plainClaims = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString()).Claims
            .Select(c => c.Type).Distinct().Order();
        Assert.Equal(["account_id", "aud", "credential_epoch", "email", "exp", "iss", "nbf", "role", "sub"], plainClaims);

        var (enrolledEmail, _) = await SeedEnrolledAsync();
        var session = await factory.LoginAsync(enrolledEmail);
        Assert.Equal("true", new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims
            .Single(c => c.Type == "two_factor").Value);

        var app = await ConnectedAppToken.IssueAsync(factory, session.AccessToken);
        using var probe = await ConnectedAppToken.Client(factory, app).GetAsync(SecondFactorFactory.OAuthProbePath);
        Assert.Equal("true", await probe.Content.ReadAsStringAsync());
    }

    // G3.9
    [Fact]
    public async Task AChallengeOlderThanFiveMinutes_IsExpired_AndChargesNothing()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);

        factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var response = await PostCodeAsync(pending.Challenge, CodeAt(factor, 0));

        var answer = await ProblemOf(response);
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), (answer.Status, answer.Title));
        var counters = await CountersAsync(factor);
        Assert.Equal((0, 0, 0L), (counters.AccessFailed, counters.DailyFailures, counters.SignIns));
    }

    // G3.12
    [Fact]
    public async Task AWrongCode_AReusedCode_AndALockedAccount_GetOneAnswer()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);
        var wrong = await ProblemOf(await PostCodeAsync(pending.Challenge, WrongCode(factor)));

        // The enrolment confirm spent the current step, so its code is a reuse.
        var reused = await ProblemOf(await PostCodeAsync(pending.Challenge, CodeAt(factor, 0)));

        for (var i = 0; i < 4; i++) await PostCodeAsync(pending.Challenge, WrongCode(factor));
        Assert.NotNull((await CountersAsync(factor)).LockoutEnd);
        var locked = await ProblemOf(await PostCodeAsync(pending.Challenge, CodeAt(factor, 1)));

        Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (wrong.Status, wrong.Title));
        Assert.Equal(wrong, reused);
        Assert.Equal(wrong, locked);
    }

    // Brief carry "Parse before digest": a malformed code is refused uncharged,
    // never a 500, and a dead challenge answers first.
    [Fact]
    public async Task AMalformedCode_IsRefusedUncharged_AfterTheChallengeIsRead()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var pending = await factory.PasswordStepAsync(email);

        string[] malformed = ["12345", "1234567", "abc-def", "BCDF-GHJK-MNPQ-RTV", "BCDF-GHJK-MNPQ-RTVA", "", "١٢٣٤٥٦"];
        foreach (var code in malformed)
        {
            var answer = await ProblemOf(await PostCodeAsync(pending.Challenge, code));
            Assert.Equal((HttpStatusCode.BadRequest, SecondFactorErrorCodes.Incorrect), (answer.Status, answer.Title));
        }
        var dead = await ProblemOf(await PostCodeAsync(pending.Challenge + "x", "12345"));

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), (dead.Status, dead.Title));
        Assert.Equal(0, (await CountersAsync(factor)).AccessFailed);
    }

    // G3.10, with the code step's own twin of AmbientPrincipalOnLoginTests (#532).
    [Fact]
    public async Task AnotherFarmsBearer_ChangesNothing_AboutWhoIsCharged_OrWhoSignsIn()
    {
        var (email, factor) = await SeedEnrolledAsync();
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreignBearer = await factory.LoginForAccessTokenAsync(foreignEmail);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreignBearer);
        var pending = await factory.PasswordStepAsync(email);

        var wrong = await client.PostAsJsonAsync(TestHarness.SecondFactorPath, new { challenge = pending.Challenge, code = WrongCode(factor) });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var charged = await CountersAsync(factor);
        Assert.Equal((1, 1), (charged.AccessFailed, charged.DailyFailures));

        await SupersedeAsync(foreignEmail);
        var right = await client.PostAsJsonAsync(TestHarness.SecondFactorPath, new { challenge = pending.Challenge, code = CodeAt(factor, 1) });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    private async Task SupersedeAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.AppDbContext>().Users
            .Where(u => u.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CredentialEpoch, u => u.CredentialEpoch + 1));
    }

    private async Task<HttpResponseMessage> GetMeAsync(string bearer)
    {
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.GetAsync("/api/v1/me");
    }

    private static List<Claim> ClaimsOf(string jwt) =>
        [.. new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
            .Where(c => c.Type is not ("aud" or "iss" or "exp" or "nbf" or "iat"))];

    private string Sign(IEnumerable<Claim> claims, string audience)
    {
        var options = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PemKey.Normalize(options.PrivateKeyPem));
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(options.Issuer, audience, claims, now, now.AddMinutes(5), credentials));
    }

    // Every route that demands an authenticated caller, with a placeholder for
    // each route parameter.
    private List<(string Method, string Path)> AuthorizedRoutes() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => e.RoutePattern.RawText is { } raw && raw.StartsWith("/api/", StringComparison.Ordinal))
            .Select(e => (
                Method: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET",
                Path: string.Join('/', e.RoutePattern.RawText!.Split('/').Select(segment =>
                    segment.StartsWith('{') ? Guid.NewGuid().ToString() : segment))))];
}
