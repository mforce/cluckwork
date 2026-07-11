namespace Cluckwork.Api.Middleware;

using Cluckwork.Infrastructure.Persistence;

// Reads account_id claim from the authenticated principal and populates TenantContext
// before any endpoint handler runs (tech spec §4.2 point 1).
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claim = context.User.FindFirst("account_id")?.Value;
            if (Guid.TryParse(claim, out var accountId))
                tenant.Resolve(accountId);
        }

        await next(context);
    }
}
