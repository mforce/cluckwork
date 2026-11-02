using System.Text.Json;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1278: one connected app for a signed-in user, with the farm read scope,
// through the real consent and token endpoints.
internal static class ConnectedAppToken
{
    public static async Task<string> IssueAsync(WebApplicationFactory<Program> host, string sessionJwt)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(
                new OpenIddictApplicationDescriptor
                {
                    ClientId = clientId,
                    ClientType = ClientTypes.Public,
                    RedirectUris = { new Uri(OAuthServerTests.RedirectUri) },
                    Permissions =
                    {
                        Permissions.Endpoints.Authorization,
                        Permissions.Endpoints.Token,
                        Permissions.GrantTypes.AuthorizationCode,
                        Permissions.ResponseTypes.Code,
                        Permissions.Prefixes.Scope + OAuthScopes.ReadFarm,
                    },
                });
        }

        var verifier = OAuthServerTests.NewCodeVerifier();
        var query = OAuthServerTests.AuthorizeQuery(clientId, verifier);
        query["scope"] = OAuthScopes.ReadFarm;
        var location = await OAuthServerTests.ApproveAsync(host, sessionJwt, query);
        using var token = await Client(host, bearer: null).PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = GrantTypes.AuthorizationCode,
                ["client_id"] = clientId,
                ["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString(),
                ["redirect_uri"] = OAuthServerTests.RedirectUri,
                ["code_verifier"] = verifier,
            }));
        token.EnsureSuccessStatusCode();
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    public static HttpClient Client(WebApplicationFactory<Program> host, string? bearer) =>
        OAuthServerTests.HttpsClient(host, bearer);
}
