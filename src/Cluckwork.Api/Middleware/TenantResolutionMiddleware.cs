namespace Cluckwork.Api.Middleware;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;

// Reads account_id claim from the authenticated principal and populates TenantContext
// before any endpoint handler runs (tech spec §4.2 point 1). Also resolves the
// acting user (sub + email) for the audit trail (#93).
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, CurrentUserContext user)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claim = context.User.FindFirst("account_id")?.Value;
            if (Guid.TryParse(claim, out var accountId))
                tenant.Resolve(accountId);

            // MapInboundClaims is off: the raw JWT claim names survive.
            var sub = context.User.FindFirst("sub")?.Value;
            var email = context.User.FindFirst("email")?.Value;
            if (Guid.TryParse(sub, out var userId))
            {
                user.Resolve(userId, email ?? "",
                    context.User.FindAll("role").Select(c => c.Value).ToList());
            }
            else
            {
                // An AUTHENTICATED principal that cannot resolve to a user is
                // rejected outright — downstream guards (flock scoping) treat
                // "unresolved" as a non-HTTP system caller and must never see
                // one over HTTP (codex review of #104).
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context);
    }
}
