namespace Cluckwork.Api.Middleware;

using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;

// Reads account_id claim from the authenticated principal and populates TenantContext
// before any endpoint handler runs (tech spec §4.2 point 1). Also resolves the
// acting user — sub, email AND ROLES (#93).
//
// The roles are not decoration for the audit row: FlockScopeGuard reads them as
// an authorization input, so this is where an HTTP request's flock scoping is
// established (#500). This is the resolver for the HTTP path only — the
// seeders and the one-shot CLI verbs resolve their own actor, because
// IAuditWriter fails closed on an unresolved one.
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, CurrentUserContext user,
        Serilog.IDiagnosticContext diagnosticContext,
        ILogger<TenantResolutionMiddleware> logger)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && !Guid.TryParse(context.User.FindFirst("account_id")?.Value, out _))
        {
            // An authenticated HTTP principal must resolve both tenant and actor
            // before flock scope. Unresolved is reserved for anonymous/non-HTTP
            // callers; the complete Tenant -> Flock order is pinned by
            // CredentialEpochMiddlewareOrderTests (#616).
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var accountScope = ResolveAccountScope(context, tenant, diagnosticContext, logger);
        if (context.User.Identity?.IsAuthenticated == true)
        {
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

    private static IDisposable? ResolveAccountScope(HttpContext context, TenantContext tenant,
        Serilog.IDiagnosticContext diagnosticContext, ILogger<TenantResolutionMiddleware> logger)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;
        var claim = context.User.FindFirst("account_id")?.Value;
        if (!Guid.TryParse(claim, out var accountId))
            return null;

        tenant.Resolve(accountId);
        // Spec §10: account_id on every log scope — rides on the request
        // completion event beside TraceId (#214)...
        diagnosticContext.Set("AccountId", accountId);
        // ...and on every event logged INSIDE the request (#216): handler
        // transition logs inherit it through the Serilog provider's MEL
        // scope handling (not FromLogContext — that serves Serilog's own
        // LogContext). Disposed with the request so the AsyncLocal scope
        // can't bleed past it.
        return logger.BeginScope(new Dictionary<string, object> { ["AccountId"] = accountId });
    }
}
