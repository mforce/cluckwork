using Cluckwork.Api.Mcp;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Authentication;
using OpenIddict.Validation.AspNetCore;

namespace Cluckwork.Api.Hosting;

// #806 — the MCP endpoint: the official SDK serving Streamable HTTP at /mcp, to OAuth
// tokens only. It exists only where the authorization server does.
internal static class CluckworkMcp
{
    public const string Path = "/mcp";

    // Generous for a JSON-RPC message: the largest planned one is the daily-entry write
    // tool's arguments, well under a kilobyte.
    public const long MaxRequestBodyBytes = 32 * 1024;

    public static void AddCluckworkMcp(this IServiceCollection services, AuthenticationBuilder authentication, Uri issuer)
    {
        // Stateless is also the SDK's default. It is set here because it is what runs a
        // tool in the HTTP request's own scope, which McpCallContext then checks.
        services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithToolsFromAssembly(typeof(McpCallContext).Assembly);

        // The scheme the bearer selector picks for every AcceptOAuthTokens endpoint. It
        // authenticates through OpenIddict validation, answers a challenge with the
        // protected-resource metadata URL (RFC 9728), and serves that document. Both URLs
        // come from the configured issuer, never the request's Host (#538).
        var resource = OAuthServerRegistration.ProtectedResource(issuer);
        authentication.AddMcp(options =>
        {
            options.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.ResourceMetadataUri = new Uri(resource, "/.well-known/oauth-protected-resource" + resource.AbsolutePath);
            options.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = resource.AbsoluteUri,
                AuthorizationServers = [issuer.AbsoluteUri],
                ScopesSupported = [.. OAuthScopes.All],
                ResourceName = "Cluckwork",
            };
        });
    }

    // Either scope admits the request; each tool then requires its own (AuthPolicies).
    public static IEndpointConventionBuilder MapCluckworkMcp(this IEndpointRouteBuilder app) =>
        app.MapMcp(Path)
            .WithMetadata(new ReadsRequestBodyAttribute(), new HandlesOwnIdempotencyAttribute())
            .WithMaxRequestBodyBytes(MaxRequestBodyBytes)
            .AcceptOAuthTokens(OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries)
            .RequireAuthorization();
}
