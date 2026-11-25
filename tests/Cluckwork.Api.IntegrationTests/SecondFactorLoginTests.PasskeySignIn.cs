using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1285: a passkey alone signs in (record 1282 decisions 3, 7, 11, 12), G8.1 to
// G8.3, G8.6 and G8.7.
public sealed partial class SecondFactorLoginTests
{
    private const string PasskeyLoginPath = "/api/v1/auth/login/passkey";

    private sealed record PasskeyUser(string Email, Guid AccountId, Guid UserId, SoftwareAuthenticator Key);

    // A user with no app and one passkey, registered through the routes, in a
    // farm of their own.
    private async Task<PasskeyUser> PasskeyUserAsync(bool counts = false)
    {
        var (email, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var key = new SoftwareAuthenticator(counts: counts);
        Assert.Equal(HttpStatusCode.OK, (await RegisterPasskeyAsync(client, key)).StatusCode);
        return new PasskeyUser(email, accountId, userId, key);
    }

    private HttpClient Anonymous() => factory.CreateClient(TestHarness.Cookieless(factory));

    private async Task<Ceremony> StartPasskeySignInAsync()
    {
        var response = await Anonymous().PostAsync(PasskeyLoginPath + "/options", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Ceremony(body.GetProperty("ceremony").GetGuid(), body.GetProperty("options").Clone());
    }

    private Task<HttpResponseMessage> PasskeySignInAsync(
        Guid ceremony, string credentialJson, string? farmCode = null, HttpClient? client = null) =>
        (client ?? Anonymous()).PostAsJsonAsync(PasskeyLoginPath,
            new { ceremony, credential = JsonDocument.Parse(credentialJson).RootElement, farmCode });

    private async Task<HttpResponseMessage> SignInWithPasskeyAsync(SoftwareAuthenticator key, string? farmCode = null)
    {
        var ceremony = await StartPasskeySignInAsync();
        return await PasskeySignInAsync(ceremony.Id, key.Assert(ceremony.Options), farmCode);
    }

    private static (Guid UserId, Guid AccountId) SubjectOf(string accessToken)
    {
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.ToList();
        return (Guid.Parse(claims.Single(c => c.Type == "sub").Value), Guid.Parse(claims.Single(c => c.Type == "account_id").Value));
    }

    private static async Task AssertPasskeyRefusedAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.Unauthorized)
    {
        Assert.Equal(status, response.StatusCode);
        var (code, title, detail) = await ProblemOf(response);
        Assert.Equal((status, PasskeyErrorCodes.Refused, "That passkey did not work. Try again."), (code, title, detail));
        Assert.False(response.Headers.Contains("Set-Cookie"), "a refused passkey set a cookie");
    }

    private Task<UserCounters> CountersOfAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserCounters(u.AccessFailedCount, u.SecondFactorFailures, u.SecondFactorSignIns,
                u.SecondFactorWatermark, u.LockoutEnd))
            .SingleAsync());

    private Task SetUserAsync(Guid accountId, Guid userId, Action<ApplicationUser> change) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            change(await db.Users.SingleAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        });

    // G8.1, G8.6: a blank farm code signs in to the passkey's farm, sets
    // SignedInAt, clears AccessFailedCount and bumps the sign-in count.
    [Fact]
    public async Task APasskeyAlone_SignsInToThePasskeysFarm_WithABlankFarmCode()
    {
        var user = await PasskeyUserAsync();
        await SetUserAsync(user.AccountId, user.UserId, u => u.AccessFailedCount = 2);
        var before = await CountersOfAsync(user.AccountId, user.UserId);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        factory.Clock.Freeze();
        factory.Sink.Events.Clear();

        var response = await SignInWithPasskeyAsync(user.Key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = await TestHarness.ReadTokensAsync(response);
        Assert.Equal((user.UserId, user.AccountId), SubjectOf(tokens.AccessToken));
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken), "a passkey sign-in set no refresh cookie");
        var after = await CountersOfAsync(user.AccountId, user.UserId);
        Assert.Equal((0, before.SignIns + 1), (after.AccessFailed, after.SignIns));
        var signedInAt = await factory.WithTenantScopeAsync(user.AccountId, db => db.RefreshTokens
            .Where(t => t.UserId == user.UserId).MaxAsync(t => t.SignedInAt));
        Assert.Equal(factory.Clock.GetUtcNow(), signedInAt);
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeySignIn));
        Assert.Empty(factory.EventsFor(SecurityEvents.PasskeyFailed));
    }

    // G8.1: another farm's code, or an unknown one, is the one refusal, and
    // no signature check runs: the passkey table is read once, by the lookup,
    // and never by Identity's handler.
    [Fact]
    public async Task AnotherFarmsCode_OrAnUnknownOne_IsTheOneRefusal_AndChecksNoSignature()
    {
        var user = await PasskeyUserAsync();
        var otherFarmCode = await factory.FarmCodeForAsync((await SignedInPlainAsync(asOwner: false)).Email);
        var other = await StartPasskeySignInAsync();
        var unknown = await StartPasskeySignInAsync();
        var reads = 0;
        factory.BeforeCommand.Arm("\"AspNetUserPasskeys\"", () =>
        {
            Interlocked.Increment(ref reads);
            return Task.CompletedTask;
        }, times: 100);

        var atOtherFarm = await PasskeySignInAsync(other.Id, user.Key.Assert(other.Options), otherFarmCode);
        var readsAtOtherFarm = reads;
        var atUnknownFarm = await PasskeySignInAsync(unknown.Id, user.Key.Assert(unknown.Options), "no-such-farm-" + Guid.NewGuid().ToString("N")[..8]);
        factory.BeforeCommand.Disarm();

        await AssertPasskeyRefusedAsync(atOtherFarm);
        await AssertPasskeyRefusedAsync(atUnknownFarm);
        Assert.Equal(1, readsAtOtherFarm);
        Assert.Equal(1, reads);
        Assert.False(await CeremonyExistsAsync(other.Id));
        Assert.True(await CeremonyExistsAsync(unknown.Id), "an unknown farm code is refused before the ceremony is consumed");
    }

    // G8.1: Identity never reads rawId, and its reader keeps the last of two
    // ids that differ only in case. Either way the attacker's own passkey
    // signed, so the attacker signs in, never the victim.
    [Fact]
    public async Task AnAssertionNamingTheVictimInRawId_SignsInTheAttacker()
    {
        var attacker = await PasskeyUserAsync();
        var victim = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();
        var json = JsonNode.Parse(attacker.Key.Assert(ceremony.Options))!;
        json["rawId"] = victim.Key.Id;

        var response = await PasskeySignInAsync(ceremony.Id, json.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((attacker.UserId, attacker.AccountId), SubjectOf((await TestHarness.ReadTokensAsync(response)).AccessToken));
    }

    [Fact]
    public async Task AnAssertionWithTheVictimsIdThenTheAttackersId_SignsInTheAttacker()
    {
        var attacker = await PasskeyUserAsync();
        var victim = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();
        var json = attacker.Key.Assert(ceremony.Options);
        var duplicated = "{\"id\":\"" + victim.Key.Id + "\"," + json[1..].Replace("\"id\":", "\"Id\":", StringComparison.Ordinal);

        var response = await PasskeySignInAsync(ceremony.Id, duplicated);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((attacker.UserId, attacker.AccountId), SubjectOf((await TestHarness.ReadTokensAsync(response)).AccessToken));
    }

    // G8.1: the signer check itself. Bob's valid assertion, over a ceremony
    // consumed for sign-in, is refused unless the expected signer is bob and
    // his credential, byte for byte.
    [Fact]
    public async Task AssertAsync_RefusesAValidAssertion_FromAnotherUserOrAnotherCredential()
    {
        var alice = await PasskeyUserAsync();
        var bob = await PasskeyUserAsync();

        async Task<AssertionOutcome> AssertAsync(ExpectedSigner expected)
        {
            var ceremony = await StartPasskeySignInAsync();
            var json = bob.Key.Assert(ceremony.Options);
            using var scope = factory.Services.CreateScope();
            var passkeys = scope.ServiceProvider.GetRequiredService<IPasskeyCredentials>();
            return await TestHarness.InTransactionAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), async ct =>
                await passkeys.AssertAsync(
                    (await passkeys.ConsumeAsync(ceremony.Id, CeremonyPurpose.SignIn, userId: null, ct))!,
                    expected, json, "localhost", ct));
        }

        Assert.IsType<AssertionOutcome.Refused>(await AssertAsync(new ExpectedSigner(alice.UserId, alice.Key.CredentialId)));
        Assert.IsType<AssertionOutcome.Refused>(await AssertAsync(new ExpectedSigner(bob.UserId, alice.Key.CredentialId)));
        Assert.IsType<AssertionOutcome.Refused>(await AssertAsync(new ExpectedSigner(alice.UserId, null)));
        var accepted = Assert.IsType<AssertionOutcome.Accepted>(await AssertAsync(new ExpectedSigner(bob.UserId, bob.Key.CredentialId)));
        Assert.Equal(bob.UserId, accepted.UserId);
    }

    // G8.2: a synced passkey reports sign count 0, so Identity alone would
    // accept the same ceremony and assertion again. The ceremony row does not.
    [Fact]
    public async Task AReplayedPasskeySignIn_IsRefused()
    {
        var user = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();
        var json = user.Key.Assert(ceremony.Options);

        Assert.Equal(HttpStatusCode.OK, (await PasskeySignInAsync(ceremony.Id, json)).StatusCode);
        await AssertPasskeyRefusedAsync(await PasskeySignInAsync(ceremony.Id, json));
    }

    [Fact]
    public async Task OneCeremonyAndOneAssertion_ThroughTwoInstances_GiveOneSession()
    {
        var user = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();
        var json = user.Key.Assert(ceremony.Options);
        var rows = await factory.WithTenantScopeAsync(user.AccountId, db => db.RefreshTokens.CountAsync(t => t.UserId == user.UserId));
        using var secondInstance = factory.WithWebHostBuilder(_ => { });

        var answers = await Task.WhenAll(
            PasskeySignInAsync(ceremony.Id, json),
            PasskeySignInAsync(ceremony.Id, json, client: secondInstance.CreateClient()));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);
        Assert.Equal(rows + 1, await factory.WithTenantScopeAsync(user.AccountId, db => db.RefreshTokens.CountAsync(t => t.UserId == user.UserId)));
    }

    // G8.3: the server requires user verification at sign-in.
    [Fact]
    public async Task APasskeySignInWithoutUserVerification_IsRefused()
    {
        var user = await PasskeyUserAsync();
        var ceremony = await StartPasskeySignInAsync();

        await AssertPasskeyRefusedAsync(await PasskeySignInAsync(ceremony.Id, user.Key.Assert(ceremony.Options, uv: false)));
    }

    // G8.4: a refused passkey charges neither the lockout nor the daily
    // budget, and logs Auth.PasskeyFailed once.
    [Fact]
    public async Task ARefusedPasskeySignIn_IsNotCharged_AndLogsOnce()
    {
        var user = await PasskeyUserAsync();
        await SetUserAsync(user.AccountId, user.UserId, u => (u.AccessFailedCount, u.SecondFactorFailures) = (2, 3));
        var before = await CountersOfAsync(user.AccountId, user.UserId);
        var ceremony = await StartPasskeySignInAsync();
        factory.Sink.Events.Clear();

        var response = await PasskeySignInAsync(ceremony.Id, user.Key.Assert(ceremony.Options, origin: "https://localhost:8443"));

        await AssertPasskeyRefusedAsync(response);
        Assert.Equal(before, await CountersOfAsync(user.AccountId, user.UserId));
        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
    }

    // G8.4: a locked user is refused with a valid passkey, and the ceremony is spent.
    [Fact]
    public async Task ALockedUser_IsRefusedWithAValidPasskey()
    {
        var user = await PasskeyUserAsync();
        await SetUserAsync(user.AccountId, user.UserId, u => u.LockoutEnd = factory.Clock.GetUtcNow().AddMinutes(15));
        var ceremony = await StartPasskeySignInAsync();
        factory.Sink.Events.Clear();

        await AssertPasskeyRefusedAsync(await PasskeySignInAsync(ceremony.Id, user.Key.Assert(ceremony.Options)));

        Assert.Single(factory.EventsFor(SecurityEvents.PasskeyFailed));
        Assert.False(await CeremonyExistsAsync(ceremony.Id));
    }

    // G8.6: a disabled user and an epoch below 1 get the one refusal.
    [Theory]
    [InlineData("disabled")]
    [InlineData("epoch")]
    public async Task ADisabledUserOrAnEpochBelowOne_IsRefused(string state)
    {
        var user = await PasskeyUserAsync();
        if (state == "disabled")
            await SetUserAsync(user.AccountId, user.UserId, u => u.DisabledAt = factory.Clock.GetUtcNow());
        else
            // The check constraint forbids the state, as CredentialEpochFloorTests drops it.
            await factory.WithTenantScopeAsync(user.AccountId, async db =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    """ALTER TABLE "AspNetUsers" DROP CONSTRAINT IF EXISTS "CK_AspNetUsers_CredentialEpoch" """);
                return await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""UPDATE "AspNetUsers" SET "CredentialEpoch" = 0 WHERE "Id" = {user.UserId}""");
            });

        await AssertPasskeyRefusedAsync(await SignInWithPasskeyAsync(user.Key));
    }

    // G8.6: the passkey's farm is read before any session is minted.
    [Fact]
    public async Task APasskeyOfASuspendedFarm_IsRefusedAsSuspended()
    {
        var user = await PasskeyUserAsync();
        using (var scope = factory.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AccountSuspensionService>().SuspendAsync(user.AccountId, "test")).IsSuccess);

        var response = await SignInWithPasskeyAsync(user.Key);

        Assert.Equal((HttpStatusCode.Unauthorized, SecondFactorErrorCodes.FarmSuspended), await TitleOf(response));
    }

    // G8.6: the sign count is written back, so a cloned counter authenticator,
    // holding the count from before the sign-in, is refused on a fresh ceremony.
    [Fact]
    public async Task AClonedCounterAuthenticator_IsRefused_AfterTheOriginalSignedIn()
    {
        var user = await PasskeyUserAsync(counts: true);
        var clone = user.Key.Clone();

        Assert.Equal(HttpStatusCode.OK, (await SignInWithPasskeyAsync(user.Key)).StatusCode);
        var stored = await factory.WithTenantScopeAsync(user.AccountId, db => db.UserPasskeys.AsNoTracking()
            .Where(p => p.UserId == user.UserId).Select(p => p.Data.SignCount).SingleAsync());
        var replay = await SignInWithPasskeyAsync(clone);

        Assert.Equal(1u, stored);
        await AssertPasskeyRefusedAsync(replay);
    }

    // G8.7: the passkey routes under /auth/login blank an ambient bearer, so
    // another farm's valid token does not change who signs in.
    [Fact]
    public async Task ThePasskeyLoginRoutes_IgnoreTheAmbientPrincipal()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/auth/login", StringComparison.Ordinal) == true
                && e.RoutePattern.RawText.Contains("passkey", StringComparison.Ordinal))
            .ToList();
        Assert.True(routes.Count >= 3, $"the walk found {routes.Count} passkey login routes, below its floor of 3");
        Assert.All(routes, e => Assert.True(e.Metadata.GetMetadata<IgnoresAmbientPrincipalAttribute>() is not null,
            $"{e.RoutePattern.RawText} keeps the ambient principal"));

        var user = await PasskeyUserAsync();
        var (_, otherAccountId, _, _, otherSession) = await SignedInPlainAsync(asOwner: true);
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherSession.AccessToken);
        var ceremony = await StartPasskeySignInAsync();

        var response = await PasskeySignInAsync(ceremony.Id, user.Key.Assert(ceremony.Options), client: client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(otherAccountId, user.AccountId);
        Assert.Equal((user.UserId, user.AccountId), SubjectOf((await TestHarness.ReadTokensAsync(response)).AccessToken));
    }
}
