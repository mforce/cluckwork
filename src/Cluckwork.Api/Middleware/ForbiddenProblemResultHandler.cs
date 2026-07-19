namespace Cluckwork.Api.Middleware;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

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
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Forbidden",
                Detail = "This action requires the Admin role.",
                Status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
