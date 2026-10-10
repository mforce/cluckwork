using Cluckwork.Api.Mcp;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Authentication;
using OpenIddict.Validation.AspNetCore;

namespace Cluckwork.Api.Hosting;

// #806 — endpoint metadata marking /mcp. The bearer selector sends its challenge through
// the MCP scheme, IdempotencyMiddleware lets its POSTs through without an Idempotency-Key,
// and its 403 and 429 responses take the shape an MCP client reads.
public sealed record McpEndpoint(Uri ResourceMetadata)
{
    public string InsufficientScopeChallenge =>
        $"Bearer error=\"insufficient_scope\", scope=\"{OAuthScopes.ReadFarm}\", resource_metadata=\"{ResourceMetadata}\"";

    public static McpEndpoint? Of(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<McpEndpoint>();
}

internal static class CluckworkMcp
{
    private const string Path = "/mcp";

    // Every MCP message is one small JSON-RPC object; the largest planned is a daily entry.
    private const long MaxRequestBodyBytes = 64 * 1024;

    public static void AddCluckworkMcp(this AuthenticationBuilder authentication, Uri issuer)
    {
        var resource = OAuthServerRegistration.McpResource(issuer);
        // RFC 9728 §3.1: the well-known segment goes between the host and the resource's path.
        var resourceMetadata = new Uri(
            resource.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource" + resource.AbsolutePath);

        // Stateless is the SDK default; pinned here because the SessionMode is what keeps a
        // tool in the HTTP request's DI scope, which McpCallContext checks (#805).
        authentication.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithToolsFromAssembly(typeof(McpCallContext).Assembly);

        // Serves the protected-resource metadata and writes /mcp's 401 challenge. The
        // token itself is validated by OpenIddict, as on every AcceptOAuthTokens endpoint.
        authentication.AddMcp(options =>
        {
            options.ForwardAuthenticate = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.ResourceMetadataUri = resourceMetadata;
            options.ResourceMetadata = new ProtectedResourceMetadata
            {
                Resource = resource.AbsoluteUri,
                AuthorizationServers = [issuer.AbsoluteUri],
                ScopesSupported = [.. OAuthScopes.All],
                ResourceName = "Cluckwork",
            };
        });

        authentication.Services.AddSingleton(new McpEndpoint(resourceMetadata));
    }

    // A tool narrows this with its own role and scope policies; tools/list hides a tool
    // whose policies the caller fails.
    public static void MapCluckworkMcp(this WebApplication app) =>
        app.MapMcp(Path)
            .WithMetadata(app.Services.GetRequiredService<McpEndpoint>(), new ReadsRequestBodyAttribute())
            .WithMaxRequestBodyBytes(MaxRequestBodyBytes)
            .AcceptOAuthTokens(OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries)
            .RequireAuthorization();
}
