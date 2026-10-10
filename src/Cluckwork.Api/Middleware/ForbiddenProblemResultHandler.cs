using Cluckwork.Api.Modules.Access.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Cluckwork.Api.Middleware;

// Role-denied requests get a problem body naming the missing role (#73) —
// the framework default is an empty 403.
public sealed class ForbiddenProblemResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            var failed = authorizeResult.AuthorizationFailure?.FailedRequirements.ToList() ?? [];
            var onlyScopesFailed = failed.Count > 0 && failed.All(r => r is OAuthScopeRequirement);

            // #806 — RFC 6750 §3.1 and the MCP scope challenge: a token that lacks a scope
            // tells its client which scopes to ask the user for. When the role failed too,
            // no new token would help, so the role body below answers instead.
            if (onlyScopesFailed)
            {
                var scopes = failed.Cast<OAuthScopeRequirement>().SelectMany(r => r.AnyOf).Distinct();
                var metadata = context.RequestServices.GetRequiredService<IOptionsMonitor<McpAuthenticationOptions>>()
                    .Get(McpAuthenticationDefaults.AuthenticationScheme).ResourceMetadataUri;
                context.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"insufficient_scope\", scope=\"{string.Join(' ', scopes)}\", resource_metadata=\"{metadata}\"";
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Forbidden",
                Detail = onlyScopesFailed
                    ? "This connection was not given the permission this action needs. Reconnect the app and allow it."
                    : "This action requires the Admin role.",
                Status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
