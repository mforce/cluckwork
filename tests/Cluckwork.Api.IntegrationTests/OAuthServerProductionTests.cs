using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

public sealed class OAuthProductionFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("AllowedHosts", "localhost");
    }
}

// #798 — Production runs the authorization server: a client registers itself, the user
// approves it with their password, and the client redeems the code. Without its public
// issuer URL a Production host refuses to start. The base factory sets the issuer.
public sealed class OAuthServerProductionTests(OAuthProductionFactory factory)
    : IClassFixture<OAuthProductionFactory>
{
    [Fact]
    public async Task Production_ServesTheConnectFlow()
    {
        var client = OAuthServerTests.HttpsClient(factory, bearer: null);
        var email = $"oauth-prod-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var jwt = await factory.LoginForAccessTokenAsync(email);
        using var registered = await client.PostAsJsonAsync("/api/v1/oauth/register",
            new { client_name = "Claude Desktop", redirect_uris = new[] { OAuthServerTests.RedirectUri } });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        var verifier = OAuthServerTests.NewCodeVerifier();
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        query["scope"] = OAuthScopes.ReadFarm;

        using var navigation = await OAuthServerTests.SendAuthorizeAsync(factory, jwt: null, query);
        using var consent = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, query);
        var location = await OAuthServerTests.ApproveAsync(factory, jwt, query);
        using var token = await client.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString(),
                ["redirect_uri"] = OAuthServerTests.RedirectUri,
                ["code_verifier"] = verifier,
            }));

        Assert.Equal(HttpStatusCode.Redirect, navigation.StatusCode);
        Assert.StartsWith("/connect?", navigation.Headers.Location!.OriginalString);
        Assert.Equal("Claude Desktop",
            (await consent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientName").GetString());
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.False(string.IsNullOrEmpty(
            (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()));
    }

    [Theory]
    [InlineData("", "OAuth:Issuer is not configured")]
    [InlineData("http://farm.example/", "OAuth:Issuer must be an absolute https URL")]
    [InlineData("https://farm.example/?tenant=1", "OAuth:Issuer must be an absolute https URL")]
    [InlineData("https://farm.example/#", "OAuth:Issuer must be an absolute https URL")]
    public void Production_WithoutAnHttpsIssuer_RefusesToStart(string issuer, string message)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("OAuth:Issuer", issuer));

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains(message, failure.ToString());
    }
}
