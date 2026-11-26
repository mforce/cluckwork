using System.Net;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1285: refusals at the edges of passkey sign-in and the second step (record
// 1282 decisions 3, 11, 12 and 13): malformed input, a ceremony of another
// purpose, a dead challenge, and the per-address limits.
public sealed partial class SecondFactorLoginTests
{
    private Task<HttpResponseMessage> RawPostAsync(string path, string json) =>
        Anonymous().PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    // A lone UTF-16 surrogate, in a property name or in the id. Escaped in the
    // JSON text, because a .NET string cannot carry one through the encoder.
    private static string LoneSurrogateCredential(string where, SoftwareAuthenticator key) => where == "name"
        ? "{\"\\ud800\":1,\"id\":\"" + key.Id + "\"}"
        : "{\"id\":\"\\ud800\"}";

    // Decision 12: malformed JSON is the one refusal, and the ceremony consumed
    // before it stays consumed.
    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    public async Task ALoneSurrogateInTheCredential_IsTheOneRefusal_AtSignIn(string where)
    {
        var user = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();
        factory.Sink.Events.Clear();

        var response = await RawPostAsync(PasskeyLoginPath,
            $"{{\"ceremony\":\"{ceremony.Id}\",\"credential\":{LoneSurrogateCredential(where, user.Key)}}}");

        await AssertPasskeyRefusedAsync(response);
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    public async Task ALoneSurrogateInTheCredential_IsTheOneRefusal_AtTheSecondStep(string where)
    {
        var user = await AppAndPasskeyUserAsync();
        var pending = await factory.PasswordStepAsync(user.Email);
        var ceremony = await StartSecondStepAsync(pending.Challenge);
        factory.Sink.Events.Clear();

        var response = await RawPostAsync(TestHarness.SecondFactorPath,
            $"{{\"challenge\":\"{pending.Challenge}\",\"passkey\":{{\"ceremony\":\"{ceremony.Id}\",\"credential\":{LoneSurrogateCredential(where, user.Key)}}}}}");

        await AssertPasskeyRefusedAsync(response, HttpStatusCode.BadRequest);
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
    }

    // A registration ceremony's challenge and relying party ID, in the shape
    // of request options, so an authenticator can sign an assertion over it.
    private static System.Text.Json.JsonElement RequestOptionsFor(System.Text.Json.JsonElement creationOptions) =>
        Credential(System.Text.Json.JsonSerializer.Serialize(new
        {
            challenge = creationOptions.GetProperty("challenge").GetString(),
            rpId = creationOptions.GetProperty("rp").GetProperty("id").GetString(),
        }));

    // Decision 3: a ceremony is good for one attempt, whatever refuses it, so
    // one finished at a route of another purpose is refused and spent.
    [Fact]
    public async Task ACeremonyOfAnotherPurpose_IsRefused_AndSpent()
    {
        var user = await AppAndPasskeyUserAsync();
        var client = factory.CreateAuthedClient((await factory.LoginAsync(user.Email)).AccessToken);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password, factory.EnrolledFactors[user.Email].RecoveryCodes[1]));
        var signInAtSecondStep = await StartPasskeySignInAsync();
        var pending = await factory.PasswordStepAsync(user.Email);
        var secondStepAtSignIn = await StartSecondStepAsync((await factory.PasswordStepAsync(user.Email)).Challenge);
        var signInAtRegistration = await StartPasskeySignInAsync();
        var registration = await StartPasskeyAsync(client);
        var registrationAtSecondStep = await StartPasskeyAsync(client);
        var pendingForRegistration = await factory.PasswordStepAsync(user.Email);

        await AssertPasskeyRefusedAsync(await PostPasskeyAnswerAsync(
            pending.Challenge, signInAtSecondStep.Id, user.Key.Assert(signInAtSecondStep.Options)), HttpStatusCode.BadRequest);
        await AssertPasskeyRefusedAsync(await PasskeySignInAsync(secondStepAtSignIn.Id, user.Key.Assert(secondStepAtSignIn.Options)));
        await AssertPasskeyRefusedAsync(await FinishPasskeyAsync(
            client, grant, signInAtRegistration.Id, new SoftwareAuthenticator().Attest(registration.Options)), HttpStatusCode.BadRequest);
        // The user's own key signs the registration ceremony's challenge, so
        // only the purpose tells it from a second-step ceremony.
        await AssertPasskeyRefusedAsync(await PostPasskeyAnswerAsync(pendingForRegistration.Challenge, registrationAtSecondStep.Id,
            user.Key.Assert(RequestOptionsFor(registrationAtSecondStep.Options))), HttpStatusCode.BadRequest);

        Assert.False(await CeremonyExistsAsync(signInAtSecondStep.Id), "a sign-in ceremony refused at the second step stayed live");
        Assert.False(await CeremonyExistsAsync(secondStepAtSignIn.Id), "a second-step ceremony refused at sign-in stayed live");
        Assert.False(await CeremonyExistsAsync(signInAtRegistration.Id), "a sign-in ceremony refused at registration stayed live");
        Assert.False(await CeremonyExistsAsync(registrationAtSecondStep.Id), "a registration ceremony refused at the second step stayed live");
    }

    // The options route refuses a challenge the code step would refuse: one
    // already answered, and one whose user was disabled after the password.
    [Fact]
    public async Task TheSecondStepOptions_RefuseAUsedChallenge_AndADisabledUsersChallenge()
    {
        var user = await AppAndPasskeyUserAsync();
        var used = await factory.PasswordStepAsync(user.Email);
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(used.Challenge, await factory.NextTotpCodeAsync(user.Email))).StatusCode);
        var pending = await factory.PasswordStepAsync(user.Email);

        // The used challenge is asked while its user is still enabled, so only
        // the challenge's binding can refuse it.
        var afterUse = await Anonymous().PostAsJsonAsync(SecondStepOptionsPath, new { challenge = used.Challenge });
        var beforeDisable = await Anonymous().PostAsJsonAsync(SecondStepOptionsPath, new { challenge = pending.Challenge });
        await SetUserAsync(user.AccountId, user.UserId, u => u.DisabledAt = factory.Clock.GetUtcNow());
        var afterDisable = await Anonymous().PostAsJsonAsync(SecondStepOptionsPath, new { challenge = pending.Challenge });

        Assert.Equal(HttpStatusCode.OK, beforeDisable.StatusCode);
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), await TitleOf(afterUse));
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), await TitleOf(afterDisable));
    }

    // A farm code holding a control character names no farm, on the password
    // route and at passkey sign-in, instead of a 500 from the lookup.
    [Fact]
    public async Task AFarmCodeWithANulCharacter_IsAnUnknownFarm_OnBothSignInRoutes()
    {
        var user = await PasskeyUserAsync();
        const string farmCode = "farm\u0000code";
        var ceremony = await StartPasskeySignInAsync();

        var password = await Anonymous().PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode, email = user.Email, password = TestHarness.Password });
        var passkey = await PasskeySignInAsync(ceremony.Id, user.Key.Assert(ceremony.Options), farmCode);

        Assert.Equal((HttpStatusCode.Unauthorized, AuthEndpoints.UnknownFarmCodeCode), await TitleOf(password));
        await AssertPasskeyRefusedAsync(passkey);
    }

    // The control-character rule reads the code as the lookup does, trimmed,
    // so padding a code with a tab, a newline or U+0085 still signs in.
    [Theory]
    [InlineData("{0}\t")]
    [InlineData("\t{0}")]
    [InlineData("{0}\n")]
    [InlineData("{0}\r\n")]
    [InlineData("{0}\u0085")]
    public async Task APaddedFarmCode_SignsIn_OnBothSignInRoutes(string padded)
    {
        var user = await PasskeyUserAsync();
        var farmCode = string.Format(System.Globalization.CultureInfo.InvariantCulture, padded, await factory.FarmCodeForAsync(user.Email));
        var password = await Anonymous().PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode, email = user.Email, password = TestHarness.Password });

        var passkey = await SignInWithPasskeyAsync(user.Key, farmCode);

        Assert.Equal(SecondFactorErrorCodes.Required, (await TitleOf(password)).Title);
        Assert.Equal(HttpStatusCode.OK, passkey.StatusCode);
    }

    // Decision 19: a passkey answer refused before VerifyAsync, for a dead
    // challenge or a suspended farm, logs one Auth.PasskeyFailed, as at sign-in.
    [Fact]
    public async Task APasskeyAnswerForADeadChallengeOrASuspendedFarm_LogsOneEvent()
    {
        var user = await AppAndPasskeyUserAsync();
        var pending = await factory.PasswordStepAsync(user.Email);
        var ceremony = await StartSecondStepAsync(pending.Challenge);
        var json = user.Key.Assert(ceremony.Options);
        factory.Sink.Events.Clear();
        var dead = await PostPasskeyAnswerAsync("not-a-challenge", ceremony.Id, json);
        var deadEvents = factory.EventsFor(SecurityEvents.PasskeyFailed).Count;
        using (var scope = factory.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Modules.Access.Identity.AccountSuspensionService>()
                .SuspendAsync(user.AccountId, "test")).IsSuccess);
        factory.Sink.Events.Clear();

        var suspended = await PostPasskeyAnswerAsync(pending.Challenge, ceremony.Id, json);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), await TitleOf(dead));
        Assert.Equal(1, deadEvents);
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.FarmSuspended), await TitleOf(suspended));
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
    }

    // A body that sends both a code and a passkey is refused before anything is
    // read, rather than one silently winning.
    [Fact]
    public async Task ASecondStepWithACodeAndAPasskey_IsRefused()
    {
        var user = await AppAndPasskeyUserAsync();
        var pending = await factory.PasswordStepAsync(user.Email);
        var ceremony = await StartSecondStepAsync(pending.Challenge);

        var response = await Anonymous().PostAsJsonAsync(TestHarness.SecondFactorPath, new
        {
            challenge = pending.Challenge,
            code = await factory.NextTotpCodeAsync(user.Email),
            passkey = new { ceremony = ceremony.Id, credential = Credential(user.Key.Assert(ceremony.Options)) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await CeremonyExistsAsync(ceremony.Id), "a refused body consumed the ceremony");
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, await factory.NextTotpCodeAsync(user.Email))).StatusCode);
    }

    // #143, #1274 decision 6, #1282 decision 13: every anonymous route under
    // /api/v1/auth carries its named per-address policy. A new anonymous route
    // fails this until it gets a row.
    [Fact]
    public void EveryAnonymousAuthRoute_CarriesItsNamedRateLimit()
    {
        var expected = new Dictionary<string, string?>
        {
            ["POST /api/v1/auth/login"] = RateLimitingOptions.LoginPolicyName,
            ["POST /api/v1/auth/login/second-factor"] = RateLimitingOptions.SecondFactorPolicyName,
            ["POST /api/v1/auth/login/second-factor/passkey-options"] = RateLimitingOptions.SecondFactorPolicyName,
            ["POST /api/v1/auth/login/passkey/options"] = RateLimitingOptions.PasskeyOptionsPolicyName,
            ["POST /api/v1/auth/login/passkey"] = RateLimitingOptions.LoginPolicyName,
            ["POST /api/v1/auth/refresh"] = RateLimitingOptions.RefreshPolicyName,
            // Logout ends a session and spends nothing a guesser wants.
            ["POST /api/v1/auth/logout"] = null,
        };
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/auth/", StringComparison.Ordinal) == true
                // The group has no authorization and the app no fallback policy,
                // so a route with no IAuthorizeData is anonymous too.
                && (e.Metadata.GetMetadata<IAllowAnonymous>() is not null || e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0))
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(method => new KeyValuePair<string, string?>(
                $"{method} {e.RoutePattern.RawText}", e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName)))
            .ToList();

        Assert.True(routes.Count >= 7, $"the walk found {routes.Count} anonymous auth routes, below its floor of 7");
        Assert.Equal(expected.OrderBy(r => r.Key), routes.OrderBy(r => r.Key));
    }
}
