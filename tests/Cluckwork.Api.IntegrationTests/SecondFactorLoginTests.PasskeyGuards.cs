using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #1284: the origin rules, the stamp rotation that ends
// earlier step-up grants, and the ceremony sweep's wiring.
public sealed partial class SecondFactorLoginTests
{
    // G7.3: a request whose host is no configured origin's host
    // runs no ceremony; the list still answers, with enabled false.
    [Fact]
    public async Task APasskeyRequestAtAHostThatMatchesNoOrigin_Is404Off()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var ceremony = await StartPasskeyAsync(client);
        HttpRequestMessage AtOtherHost(HttpRequestMessage request)
        {
            request.Headers.Host = "cluckwork.test";
            return request;
        }
        var finish = AtOtherHost(new HttpRequestMessage(HttpMethod.Post, PasskeysPath)
        {
            Content = JsonContent.Create(new
            {
                ceremony = ceremony.Id,
                credential = Credential(new SoftwareAuthenticator().Attest(ceremony.Options)),
                name = "Phone",
            }),
        });
        finish.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);

        var options = await client.SendAsync(AtOtherHost(new HttpRequestMessage(HttpMethod.Post, PasskeysPath + "/options")));
        var finished = await client.SendAsync(finish);
        var listed = await client.SendAsync(AtOtherHost(new HttpRequestMessage(HttpMethod.Get, PasskeysPath)));

        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(options));
        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(finished));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.False((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, userId));
        Assert.True(await CeremonyExistsAsync(ceremony.Id), "a refusal before the transaction consumed the ceremony");
    }

    // Decision 5: the origin hook accepts only a configured
    // origin, port exact, and never a cross-origin create.
    [Fact]
    public Task APasskeyFromAnotherPortOfTheSameHost_IsRefused() =>
        AssertRefusedPasskeyAttestationAsync(options => new SoftwareAuthenticator().Attest(options, origin: "https://localhost:8443"));

    [Fact]
    public Task ACrossOriginPasskey_IsRefused() =>
        AssertRefusedPasskeyAttestationAsync(options => new SoftwareAuthenticator().Attest(options, crossOrigin: true));

    // Decision 16: a first passkey and a removal rotate the
    // security stamp, which is what ends a step-up grant minted before them.
    [Fact]
    public async Task AFirstPasskey_EndsAStepUpGrantMintedBeforeIt()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var earlier = await GrantOf(await StepUpAsync(client, TestHarness.Password));

        Assert.Equal(HttpStatusCode.OK, (await RegisterPasskeyAsync(client, new SoftwareAuthenticator())).StatusCode);

        Assert.True((await ValidateGrantAsync(accountId, userId, earlier!)).IsFailure, "a grant minted before the first passkey survived it");
    }

    [Fact]
    public async Task RemovingAPasskey_EndsAStepUpGrantMintedBeforeIt()
    {
        var (factor, client, _, id) = await AppUserWithPasskeyAsync();
        var spent = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[0]));
        var earlier = await GrantOf(await StepUpAsync(client, TestHarness.Password, factor.RecoveryCodes[1]));

        Assert.Equal(HttpStatusCode.OK, (await RemovePasskeyAsync(client, id, spent)).StatusCode);

        Assert.True((await ValidateGrantAsync(factor.AccountId, factor.UserId, earlier!)).IsFailure, "a grant minted before the removal survived it");
    }

    // Decision 3: the job worker, built from the host's services
    // as the host builds it, runs the ceremony sweep.
    [Fact]
    public async Task TheJobWorker_RunsThePasskeyCeremonySweep()
    {
        var expired = new PasskeyCeremony
        {
            Id = Guid.NewGuid(), Purpose = CeremonyPurpose.Registration, State = "state",
            ExpiresAt = factory.Clock.GetUtcNow() - TimeSpan.FromSeconds(1),
        };
        await CeremoniesAsync(db =>
        {
            db.PasskeyCeremonies.Add(expired);
            return db.SaveChangesAsync();
        });
        var lease = new SignallingLease(LeaseStatus.Leader);
        var worker = ActivatorUtilities.CreateInstance<DurableJobWorker>(factory.Services, lease, (TimeSpan?)TimeSpan.FromMilliseconds(20));

        await lease.RunWorkerThroughOneIterationAsync(worker);

        Assert.False(await CeremonyExistsAsync(expired.Id), "the worker's poll did not sweep an expired passkey ceremony");
    }
}
