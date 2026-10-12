using System.Reflection;
using System.Text.Json.Nodes;
using Cluckwork.Api.Mcp;
using Cluckwork.Api.Middleware;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Server;
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
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpToolErrors.Filter));
        AddTools(services);

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

    // WithToolsFromAssembly, plus the schema options it does not take (#1230).
    private static void AddTools(IServiceCollection services)
    {
        var schema = new AIJsonSchemaCreateOptions { TransformSchemaNode = WithoutNull };
        foreach (var type in typeof(McpCallContext).Assembly.GetTypes().Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute))))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                     .Where(m => m.IsDefined(typeof(McpServerToolAttribute))))
            services.AddSingleton(sp =>
            {
                var options = new McpServerToolCreateOptions { Services = sp, SchemaCreateOptions = schema };
                return method.IsStatic
                    ? McpServerTool.Create(method, target: null, options)
                    : McpServerTool.Create(method, r => ActivatorUtilities.CreateInstance(r.Services!, type), options);
            });
    }

    // An omitted parameter binds to its default, so a client never needs to send null.
    // Some clients mishandle a ["string","null"] union, and a nullable reference type's
    // element or property reads as nullable to the schema exporter whatever it declares.
    private static JsonNode WithoutNull(AIJsonSchemaCreateContext context, JsonNode node)
    {
        if (node is JsonObject schema && schema["type"] is JsonArray types)
        {
            string[] rest = [.. types.Select(t => t!.GetValue<string>()).Where(t => t != "null")];
            schema["type"] = rest.Length == 1 ? rest[0] : new JsonArray([.. rest.Select(t => JsonValue.Create(t))]);
            if (schema.TryGetPropertyValue("default", out var value) && value is null)
                schema.Remove("default");
        }
        return node;
    }

    // RFC 6750 §3 and RFC 9728 §5.1: a refused OAuth token is answered with a bearer
    // challenge naming the protected-resource metadata, which is how an MCP client
    // re-authorizes. The SDK's handler adds it to a challenge; the shared request checks
    // and the scope gate write their own responses and call this. Other endpoints take
    // session JWTs, which have no protected-resource metadata.
    public static void AddChallenge(HttpContext context, string parameters)
    {
        if (!OAuthEndpoints.AcceptsOAuthTokens(context.GetEndpoint()))
            return;
        var metadata = context.RequestServices.GetRequiredService<IOptionsMonitor<McpAuthenticationOptions>>()
            .Get(McpAuthenticationDefaults.AuthenticationScheme).ResourceMetadataUri;
        context.Response.Headers.WWWAuthenticate = $"Bearer {parameters}, resource_metadata=\"{metadata}\"";
    }

    // Either scope admits the request; each tool then requires its own (AuthPolicies).
    public static void MapCluckworkMcp(this IEndpointRouteBuilder app)
    {
        app.MapMcp(Path)
            .WithMetadata(new ReadsRequestBodyAttribute(), new HandlesOwnIdempotencyAttribute())
            .WithMaxRequestBodyBytes(MaxRequestBodyBytes)
            .AcceptOAuthTokens(OAuthScopes.ReadFarm, OAuthScopes.WriteDailyEntries)
            .RequireAuthorization();

        // Stateless serves no GET stream, and the MCP spec then wants 405. Without this the
        // SPA fallback, which takes every GET and HEAD, would answer with the app's HTML.
        app.MapMethods(Path, [HttpMethods.Get, HttpMethods.Head], (HttpResponse response) =>
            {
                response.Headers.Allow = HttpMethods.Post;
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            })
            .ExcludeFromDescription();
    }

    // MCP transports require Origin validation against DNS rebinding, for every request to
    // the endpoint. It runs before authentication and rate limiting, so a foreign page gets
    // 403 whatever it sends. A native client sends no Origin; a browser sends its page's.
    // Keyed on the path, not the endpoint, because a method /mcp does not map (DELETE, PUT,
    // OPTIONS) selects routing's own 405 endpoint. Matched as routing matches the route:
    // ignoring case, with one optional trailing slash.
    public static void UseMcpOriginCheck(this IApplicationBuilder app, Uri issuer)
    {
        var required = new RequiredOrigin(issuer);
        app.Use((context, next) =>
        {
            var path = context.Request.Path.Value;
            if (!(string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, Path + "/", StringComparison.OrdinalIgnoreCase))
                || required.Allows(context.Request.Headers.Origin))
                return next(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
    }

    // The configured issuer's origin, never one derived from the request's Host. An origin
    // is a scheme, host and port (RFC 6454), and a browser sends it once, ASCII-serialized,
    // so the host compares as punycode and an explicit default port equals an omitted one.
    internal sealed class RequiredOrigin(Uri issuer)
    {
        public bool Allows(StringValues sent) =>
            sent.Count == 0
            || (sent.Count == 1 && Parse(sent[0]) is { } origin
                && origin.Scheme == issuer.Scheme
                && string.Equals(origin.IdnHost, issuer.IdnHost, StringComparison.OrdinalIgnoreCase)
                && origin.Port == issuer.Port);

        // scheme://host[:port] and nothing more: no path, query, fragment or credentials. The
        // opaque origin "null" is not an absolute URI, so it fails here.
        private static Uri? Parse(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && value.StartsWith(uri.Scheme + Uri.SchemeDelimiter, StringComparison.OrdinalIgnoreCase)
            && value.AsSpan(uri.Scheme.Length + Uri.SchemeDelimiter.Length).IndexOfAny("/?#@\\") < 0
                ? uri
                : null;
    }
}
