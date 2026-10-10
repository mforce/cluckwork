using Cluckwork.Api.Hosting;
using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

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
            // #806 — RFC 6750 §3.1: a token holding none of /mcp's scopes is told which to
            // ask for, so an MCP client can reauthorize instead of giving up.
            var mcp = McpEndpoint.Of(context) is { } endpoint && !OAuthScopes.All.Any(context.User.HasScope)
                ? endpoint
                : null;
            if (mcp is not null)
                context.Response.Headers.WWWAuthenticate = mcp.InsufficientScopeChallenge;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Forbidden",
                Detail = mcp is null
                    ? "This action requires the Admin role."
                    : "This connection was not granted access to farm data.",
                Status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
