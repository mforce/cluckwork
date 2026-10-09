using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests;

// #798 — consent: all or nothing, approved with the user's password through a step-up
// grant, skipped when the app asks for nothing new, asked again when it asks for more.
[Collection(IntegrationCollection.Name)]
public sealed class OAuthConsentTests(CluckworkWebApplicationFactory factory)
{
    private const string Read = OAuthScopes.ReadFarm;
    private const string Write = OAuthScopes.WriteDailyEntries;

    [Fact]
    public async Task FirstRequest_AsksForConsent_AndIssuesNothing()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await RegisterNamedClientAsync("Claude Desktop");

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read, Write));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("redirectUri", out _), "a code was issued without consent");
        Assert.Equal(clientId, body.GetProperty("clientId").GetString());
        Assert.Equal("Claude Desktop", body.GetProperty("clientName").GetString());
        Assert.Equal("client.example", body.GetProperty("redirectHost").GetString());
        Assert.Equal([Read, Write], Strings(body, "scopes"));
        Assert.Empty(Strings(body, "alreadyAllowed"));
        Assert.False(body.GetProperty("alreadyApproved").GetBoolean(), "a first request skipped the permissions");
        Assert.Equal(0, await CountAuthorizationsAsync(userId));
    }

    [Fact]
    public async Task Approval_WithoutAValidGrant_IsRefused()
    {
        const string grant = "not-a-grant";
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), stepUp: grant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StepUpErrorCodes.Required,
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(0, await CountAuthorizationsAsync(userId));
    }

    // Single use: the grant that approved one app cannot approve another.
    [Fact]
    public async Task Approval_SpendsTheGrant()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var first = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var second = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var grant = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var approved = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(first, Read), stepUp: grant);
        using var replayed = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(second, Read), stepUp: grant);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
    }

    // fetch cannot read a cross-origin redirect, so the SPA gets it as JSON, carrying
    // everything the standard redirect would.
    [Fact]
    public async Task Approval_ReturnsTheClientRedirect_AsJson()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var verifier = OAuthServerTests.NewCodeVerifier();

        var location = await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, [Read], verifier));

        Assert.Equal(OAuthServerTests.RedirectUri, location.GetLeftPart(UriPartial.Path));
        var parameters = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("state-795", parameters["state"].ToString());
        Assert.Equal("https://localhost/", parameters["iss"].ToString());
        using var redeemed = await RedeemAsync(clientId, parameters["code"].ToString(), verifier);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
    }

    // #798 — the maintainer's decision: a reconnect skips the permissions, never the
    // password. The code becomes a token that never expires, so a session bearer alone
    // must not mint one, even for an app the user already approved.
    [Fact]
    public async Task ApprovedApp_AsksOnlyForThePassword()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read, Write));

        using var bearerOnly = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read));
        var body = await bearerOnly.Content.ReadFromJsonAsync<JsonElement>();
        var location = await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read));

        Assert.False(body.TryGetProperty("redirectUri", out _), "a session bearer alone minted a code for an approved app");
        Assert.True(body.GetProperty("alreadyApproved").GetBoolean(), "an approved app was shown its permissions again");
        Assert.False(string.IsNullOrEmpty(QueryHelpers.ParseQuery(location.Query)["code"]), "the reconnect issued no code");
        Assert.Equal(1, await CountAuthorizationsAsync(userId));
    }

    [Fact]
    public async Task Reconnect_SpendsTheGrant()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read));
        var grant = await OAuthServerTests.StepUpAsync(factory, jwt);

        using var reconnected = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), stepUp: grant);
        using var replayed = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), stepUp: grant);

        Assert.Equal(HttpStatusCode.OK, reconnected.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
    }

    [Fact]
    public async Task MoreScopes_AsksAgain_NamingWhatIsAlreadyAllowed()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read));

        using var wider = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read, Write));

        var body = await wider.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("alreadyApproved").GetBoolean(), "a wider request skipped the permissions");
        Assert.Equal([Read, Write], Strings(body, "scopes"));
        Assert.Equal([Read], Strings(body, "alreadyAllowed"));
    }

    [Fact]
    public async Task AnotherUsersApproval_DoesNotSkip()
    {
        var flows = new OAuthServerTests(factory);
        var (_, approver) = await flows.SeedUserAsync();
        var (_, other) = await flows.SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, approver, Query(clientId, Read));

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, other, Query(clientId, Read));

        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyApproved").GetBoolean(),
            "another user's approval skipped the permissions");
    }

    [Fact]
    public async Task DisconnectedApproval_DoesNotSkip()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        await OAuthServerTests.ApproveAsync(factory, jwt, Query(clientId, Read));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await foreach (var authorization in authorizations.FindBySubjectAsync(userId.ToString()))
                Assert.True(await authorizations.TryRevokeAsync(authorization), "the authorization was not revoked");
        }

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("alreadyApproved").GetBoolean(), "a revoked approval skipped the permissions");
        Assert.Empty(Strings(body, "alreadyAllowed"));
    }

    [Fact]
    public async Task Cancel_SendsAccessDeniedToTheClient_AndRecordsNothing()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, Read), consent: "deny");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var location = await RedirectFromAsync(response, "cancel did not send the user back to the client");
        var parameters = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(Errors.AccessDenied, parameters["error"].ToString());
        Assert.Equal("state-795", parameters["state"].ToString());
        Assert.Equal(0, await CountAuthorizationsAsync(userId));
    }

    // The SPA refreshes on a 401 and asks again; a redirect to /connect would hand fetch
    // the SPA's HTML instead. The token is the user's real session, signed, and expired
    // past the 30-second clock skew.
    [Fact]
    public async Task ExpiredSession_IsUnauthorized_NotARedirect()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var session = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        var expired = CredentialEpochTests.CreateAccessToken(
            userId,
            Guid.Parse(session.Claims.First(claim => claim.Type == "account_id").Value),
            session.Claims.First(claim => claim.Type == "credential_epoch").Value,
            expiresUtc: DateTime.UtcNow.AddMinutes(-2));
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, expired, Query(clientId, Read));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBearer_IsUnauthorized_NotARedirect()
    {
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, "not.a.session", Query(clientId, Read));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A client with one registered redirect URI may omit redirect_uri (OAuth 2.1 §2.3.2);
    // OpenIddict then uses the registered one.
    [Fact]
    public async Task OmittedRedirectUri_UsesTheRegisteredOne()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var query = Query(clientId, Read);
        query.Remove("redirect_uri");

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("client.example",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("redirectHost").GetString());
    }

    [Fact]
    public async Task NoScope_AsksForReadOnly()
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId));

        Assert.Equal([Read], Strings(await response.Content.ReadFromJsonAsync<JsonElement>(), "scopes"));
    }

    // Only the two registered scopes exist; OpenIddict refuses anything else before the
    // redirect URI is trusted, so it answers directly.
    [Fact]
    public async Task UnknownScope_IsRefused()
    {
        var (userId, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, Query(clientId, "farm:admin"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"error:{Errors.InvalidScope}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountAuthorizationsAsync(userId));
    }

    // The SPA receives the client's redirect as one URL, which only query mode is.
    [Theory]
    [InlineData(ResponseModes.FormPost)]
    [InlineData(ResponseModes.Fragment)]
    public async Task ResponseModeOtherThanQuery_IsRefused(string mode)
    {
        var (_, jwt) = await new OAuthServerTests(factory).SeedUserAsync();
        var clientId = await OAuthServerTests.RegisterClientAsync(factory.Services);
        var query = Query(clientId, Read);
        query["response_mode"] = mode;

        using var response = await OAuthServerTests.SendAuthorizeAsync(factory, jwt, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Behind a proxy the request's Host can be an internal name; clients must be sent to
    // the public one.
    [Fact]
    public async Task Discovery_NamesEndpointsUnderTheIssuer()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // A loopback name host filtering admits, and not the issuer's host.
            BaseAddress = new Uri("https://127.0.0.1"),
        });

        var metadata = await client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.Equal("https://localhost/api/v1/oauth/authorize", metadata.GetProperty("authorization_endpoint").GetString());
        Assert.Equal("https://localhost/api/v1/oauth/token", metadata.GetProperty("token_endpoint").GetString());
        Assert.Equal("https://localhost/api/v1/oauth/register", metadata.GetProperty("registration_endpoint").GetString());
    }

    private static async Task<Uri> RedirectFromAsync(HttpResponseMessage response, string otherwise)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("redirectUri", out _), otherwise);
        return OAuthServerTests.RedirectOf(body);
    }

    private static Dictionary<string, string?> Query(string clientId, params string[] scopes) =>
        Query(clientId, scopes, OAuthServerTests.NewCodeVerifier());

    private static Dictionary<string, string?> Query(string clientId, string[] scopes, string verifier)
    {
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        if (scopes.Length > 0) query["scope"] = string.Join(' ', scopes);
        return query;
    }

    private async Task<string> RegisterNamedClientAsync(string name)
    {
        using var response = await OAuthServerTests.HttpsClient(factory, bearer: null).PostAsJsonAsync(
            "/api/v1/oauth/register", new { client_name = name, redirect_uris = new[] { OAuthServerTests.RedirectUri } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private Task<HttpResponseMessage> RedeemAsync(string clientId, string code, string verifier) =>
        OAuthServerTests.HttpsClient(factory, bearer: null).PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthServerTests.RedirectUri,
                ["code_verifier"] = verifier,
            }));

    private async Task<int> CountAuthorizationsAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var count = 0;
        await foreach (var _ in scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
                           .FindBySubjectAsync(userId.ToString()))
            count++;
        return count;
    }

    private static string[] Strings(JsonElement body, string name) =>
        [.. body.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];
}
