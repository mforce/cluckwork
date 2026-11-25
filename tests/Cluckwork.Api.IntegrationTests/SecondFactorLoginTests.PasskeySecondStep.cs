using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Api.IntegrationTests;

// #1285: a passkey answers the second sign-in step (record 1282 decision 7),
// G8.3 to G8.5.
public sealed partial class SecondFactorLoginTests
{
    private const string SecondStepOptionsPath = TestHarness.SecondFactorPath + "/passkey-options";

    // A user with the app and one passkey, registered through the routes.
    private async Task<PasskeyUser> AppAndPasskeyUserAsync()
    {
        var (email, factor) = await SeedEnrolledAsync(asOwner: false);
        var client = factory.CreateAuthedClient((await factory.LoginAsync(email)).AccessToken);
        var key = new SoftwareAuthenticator();
        Assert.Equal(HttpStatusCode.Created, (await RegisterPasskeyAsync(client, key, factor.RecoveryCodes[0])).StatusCode);
        return new PasskeyUser(email, factor.AccountId, factor.UserId, key);
    }

    private async Task<(PendingLogin Pending, string[] SecondFactors)> PasswordStepWithFactorsAsync(string email)
    {
        var response = await factory.TryLoginAsync(email, TestHarness.Password);
        var factors = (await TestHarness.ReadProblemAsync(response)).GetProperty("secondFactors")
            .EnumerateArray().Select(f => f.GetString()!).ToArray();
        return (await TestHarness.ReadPendingLoginAsync(response), factors);
    }

    private async Task<Ceremony> StartSecondStepAsync(string challenge)
    {
        var response = await Anonymous().PostAsJsonAsync(SecondStepOptionsPath, new { challenge });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Ceremony(body.GetProperty("ceremony").GetGuid(), body.GetProperty("options").Clone());
    }

    private Task<HttpResponseMessage> PostPasskeyAnswerAsync(string challenge, Guid ceremony, string credentialJson) =>
        Anonymous().PostAsJsonAsync(TestHarness.SecondFactorPath,
            new { challenge, passkey = new { ceremony, credential = JsonDocument.Parse(credentialJson).RootElement } });

    // The second step answered with the user's passkey, uv as given.
    private async Task<HttpResponseMessage> AnswerWithPasskeyAsync(string challenge, SoftwareAuthenticator key, bool uv = true)
    {
        var ceremony = await StartSecondStepAsync(challenge);
        return await PostPasskeyAnswerAsync(challenge, ceremony.Id, key.Assert(ceremony.Options, uv: uv));
    }

    // G8.5: the password alone never signs in a user with a passkey. The
    // pending state lists the passkey only, a code is refused, and a passkey
    // bound to the challenge's user completes it.
    [Fact]
    public async Task APasskeyUsersPassword_GetsThePendingState_AndOnlyAPasskeyCompletesIt()
    {
        var user = await PasskeyUserAsync();

        var (pending, factors) = await PasswordStepWithFactorsAsync(user.Email);
        var code = await PostCodeAsync(pending.Challenge, "123456");
        var ceremony = await StartSecondStepAsync(pending.Challenge);
        var answer = await PostPasskeyAnswerAsync(pending.Challenge, ceremony.Id, user.Key.Assert(ceremony.Options));

        Assert.Equal([SecondFactorKinds.Passkey], factors);
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), await TitleOf(code));
        Assert.Equal([user.Key.Id], ceremony.Options.GetProperty("allowCredentials").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal((user.UserId, user.AccountId), SubjectOf((await TestHarness.ReadTokensAsync(answer)).AccessToken));
    }

    // G8.5: a user with the app and a passkey may answer with either.
    [Fact]
    public async Task AUserWithTheAppAndAPasskey_MayAnswerWithEither()
    {
        var user = await AppAndPasskeyUserAsync();

        var (byPasskey, factors) = await PasswordStepWithFactorsAsync(user.Email);
        var passkeyAnswer = await AnswerWithPasskeyAsync(byPasskey.Challenge, user.Key);
        var byCode = await factory.PasswordStepAsync(user.Email);
        var codeAnswer = await PostCodeAsync(byCode.Challenge, await factory.NextTotpCodeAsync(user.Email));

        Assert.Equal([SecondFactorKinds.Authenticator, SecondFactorKinds.Passkey], factors);
        Assert.Equal(HttpStatusCode.OK, passkeyAnswer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, codeAnswer.StatusCode);
    }

    // G8.5: one accepted proof of either kind ends its challenge and every
    // challenge issued before it.
    [Fact]
    public async Task AnAcceptedPasskey_EndsItsChallenge_AndOlderOnes()
    {
        var user = await AppAndPasskeyUserAsync();
        var older = await factory.PasswordStepAsync(user.Email);
        var used = await factory.PasswordStepAsync(user.Email);

        Assert.Equal(HttpStatusCode.OK, (await AnswerWithPasskeyAsync(used.Challenge, user.Key)).StatusCode);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired),
            await TitleOf(await PostCodeAsync(older.Challenge, await factory.NextTotpCodeAsync(user.Email))));
        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired),
            await TitleOf(await PostCodeAsync(used.Challenge, await factory.NextTotpCodeAsync(user.Email))));
    }

    [Fact]
    public async Task AnAcceptedCode_EndsAnOlderChallengesPasskeyAnswer()
    {
        var user = await AppAndPasskeyUserAsync();
        var older = await factory.PasswordStepAsync(user.Email);
        var ceremony = await StartSecondStepAsync(older.Challenge);

        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync((await factory.PasswordStepAsync(user.Email)).Challenge, await factory.NextTotpCodeAsync(user.Email))).StatusCode);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired),
            await TitleOf(await PostPasskeyAnswerAsync(older.Challenge, ceremony.Id, user.Key.Assert(ceremony.Options))));
    }

    // G8.5: a passkey sign-in ends a pending password challenge, as an accepted code does.
    [Fact]
    public async Task APasskeySignIn_EndsAPendingPasswordChallenge()
    {
        var user = await AppAndPasskeyUserAsync();
        var pending = await factory.PasswordStepAsync(user.Email);

        Assert.Equal(HttpStatusCode.OK, (await SignInWithPasskeyAsync(user.Key)).StatusCode);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired),
            await TitleOf(await PostCodeAsync(pending.Challenge, await factory.NextTotpCodeAsync(user.Email))));
    }

    // G8.3, G8.4: an answer without user verification is refused with 400,
    // so apiFetch does not refresh and resend; nothing is charged, the
    // challenge stays usable, and Auth.PasskeyFailed is logged once.
    [Fact]
    public async Task APasskeyAnswerWithoutUserVerification_IsRefused_AndNotCharged()
    {
        var user = await AppAndPasskeyUserAsync();
        await SetUserAsync(user.AccountId, user.UserId, u => u.AccessFailedCount = 2);
        var pending = await factory.PasswordStepAsync(user.Email);
        var before = await CountersOfAsync(user.AccountId, user.UserId);
        factory.Sink.Events.Clear();

        var refused = await AnswerWithPasskeyAsync(pending.Challenge, user.Key, uv: false);

        await AssertPasskeyRefusedAsync(refused, HttpStatusCode.BadRequest);
        Assert.Equal(before, await CountersOfAsync(user.AccountId, user.UserId));
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
        Assert.Equal(HttpStatusCode.OK, (await PostCodeAsync(pending.Challenge, await factory.NextTotpCodeAsync(user.Email))).StatusCode);
    }

    // Decision 3: the options route reads the challenge without spending it,
    // and a dead one is the code step's 401.
    [Fact]
    public async Task TheSecondStepOptions_RefuseADeadChallenge()
    {
        var response = await Anonymous().PostAsJsonAsync(SecondStepOptionsPath, new { challenge = "not-a-challenge" });

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.ChallengeExpired), await TitleOf(response));
    }
}
