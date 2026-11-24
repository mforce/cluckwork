using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Api.Modules.Access.Auth;

namespace Cluckwork.Api.IntegrationTests;

// #1284: how the passkey routes read the request's host. They take it from
// Request.Host.Host, which drops the port and decodes punycode, so the Off
// check, Identity's relying party ID and the origin hook all read one value.
public sealed partial class SecondFactorLoginTests
{
    private static HttpRequestMessage AtHost(HttpMethod method, string path, string host, object? body = null, string? grant = null)
    {
        var request = new HttpRequestMessage(method, path) { Headers = { Host = host } };
        if (body is not null) request.Content = JsonContent.Create(body);
        if (grant is not null) request.Headers.Add(AuthEndpoints.StepUpHeaderName, grant);
        return request;
    }

    // Decision 5: the origin's host is compared without the port, so a Host
    // header with a port still runs the ceremony, and the passkey registers.
    [Fact]
    public async Task APasskeyRequestWhoseHostHasAPort_Registers()
    {
        var (_, accountId, userId, client, _) = await SignedInPlainAsync(asOwner: false);
        var grant = await GrantOf(await StepUpAsync(client, TestHarness.Password));

        var options = await client.SendAsync(AtHost(HttpMethod.Post, PasskeysPath + "/options", "localhost:8443"));
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var body = await options.Content.ReadFromJsonAsync<JsonElement>();
        var finish = await client.SendAsync(AtHost(HttpMethod.Post, PasskeysPath, "localhost:8443", new
        {
            ceremony = body.GetProperty("ceremony").GetGuid(),
            credential = JsonDocument.Parse(new SoftwareAuthenticator().Attest(body.GetProperty("options").Clone())).RootElement,
            name = "Phone",
        }, grant));

        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        Assert.Equal((1, 1), await factory.PasskeyStateAsync(accountId, userId));
    }

    // An internationalised host is not supported: the request's host is read
    // decoded (hühner.example), while a configured punycode origin keeps its
    // ASCII form, so the two never match and the routes answer as off. Pinned
    // so the Off check, the relying party ID and the origin hook keep reading
    // the same value.
    [Fact]
    public async Task APasskeyRequestAtAPunycodeHost_IsOff()
    {
        using var idn = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Passkeys:Origins:1", "https://xn--hhner-kva.example")
            .UseSetting("AllowedHosts", "cluckwork.test;localhost;xn--hhner-kva.example"));
        var (_, accountId, userId, _, session) = await SignedInPlainAsync(asOwner: false);
        var client = idn.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var options = await client.SendAsync(AtHost(HttpMethod.Post, PasskeysPath + "/options", "xn--hhner-kva.example"));
        var listed = await client.SendAsync(AtHost(HttpMethod.Get, PasskeysPath, "xn--hhner-kva.example"));

        Assert.Equal((HttpStatusCode.NotFound, "Passkeys.Off"), await TitleOf(options));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.False((await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.Equal((0, 0), await factory.PasskeyStateAsync(accountId, userId));
    }
}
