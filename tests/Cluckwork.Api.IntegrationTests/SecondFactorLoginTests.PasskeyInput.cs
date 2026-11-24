using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluckwork.Api.IntegrationTests.Infrastructure;

namespace Cluckwork.Api.IntegrationTests;

// #1284: what a finish refuses in the browser's credential and at the origin.
public sealed partial class SecondFactorLoginTests
{
    // Postgres refuses \u0000 in the jsonb Data column, and any string the
    // credential carries can hold one, so the finish refuses it once for the
    // whole credential, with the one body, and the ceremony stays spent.
    private async Task AssertANulInThePasskeyCredentialIsRefusedAsync(Action<JsonNode> addNul)
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));
        var ceremony = await StartPasskeyAsync(client);
        var credential = JsonNode.Parse(new SoftwareAuthenticator().Attest(ceremony.Options))!;
        addNul(credential);

        var response = await FinishPasskeyAsync(client, grant, ceremony.Id, credential.ToJsonString());

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(response));
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, userId));
        Assert.False(await CeremonyExistsAsync(ceremony.Id), "a refused finish left its ceremony");
    }

    [Fact]
    public Task ANulInThePasskeyTransports_IsRefused() =>
        AssertANulInThePasskeyCredentialIsRefusedAsync(credential => credential["response"]!["transports"] = new JsonArray("internal\0"));

    [Fact]
    public Task ANulInThePasskeyAttachment_IsRefused() =>
        AssertANulInThePasskeyCredentialIsRefusedAsync(credential => credential["authenticatorAttachment"] = "plat\0form");

    // Decision 5: with two origins configured, a create whose client origin is
    // the other configured origin, not the request's own host, is refused; the
    // request's own origin registers, and the list shows its address.
    [Fact]
    public async Task APasskeyMadeAtTheOtherConfiguredOrigin_IsRefused()
    {
        using var twoOrigins = factory.WithWebHostBuilder(builder => builder.UseSetting("Passkeys:Origins:1", "https://cluckwork.test"));
        var (_, accountId, userId, _, session) = await SignedInPlainAsync(asOwner: false);
        var client = twoOrigins.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var other = await StartPasskeyAsync(client);
        var own = await StartPasskeyAsync(client);

        var atOther = await FinishPasskeyAsync(client, await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            other.Id, new SoftwareAuthenticator().Attest(other.Options, origin: "https://cluckwork.test"));
        var atOwn = await FinishPasskeyAsync(client, await GrantOf(await StepUpAsync(client, TestHarness.Password)),
            own.Id, new SoftwareAuthenticator().Attest(own.Options));

        Assert.Equal((HttpStatusCode.BadRequest, "Auth.PasskeyRefused"), await TitleOf(atOther));
        Assert.Equal(HttpStatusCode.OK, atOwn.StatusCode);
        Assert.Equal("https://localhost",
            (await atOwn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("passkey").GetProperty("address").GetString());
        Assert.Equal((1, 1), await factory.PasskeyStateAsync(accountId, userId));
    }
}
