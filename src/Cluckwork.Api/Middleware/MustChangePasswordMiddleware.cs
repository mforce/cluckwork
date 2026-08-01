namespace Cluckwork.Api.Middleware;

using Microsoft.AspNetCore.Mvc;

// #283 — the server-side half of the first-run "you must set a new password"
// gate. TenantResolutionMiddleware (immediately before this in Program.cs's
// pipeline) has already read the authenticated principal's claims; this reads
// the "must_change_password" claim JwtTokenService adds ONLY when
// ApplicationUser.MustChangePassword is true. While present, every endpoint
// except the two on the allowlist below is refused with 403 — BEFORE
// UseAuthorization and UseMiddleware&lt;IdempotencyMiddleware&gt;, so a blocked
// write consumes no idempotency key and a role-specific policy never even
// gets asked.
//
// This is enforcement, not just UI: claims.ts decodes the same claim so the
// SPA can show its first-login screen instead of the normal app shell, but
// "This decode drives UI visibility only; the API enforces the policy on
// every gated endpoint" (claims.ts's own header comment) applies here too — a
// caller that skips the SPA and hits the API directly is refused exactly the
// same way.
//
// Deliberately path-based, not a per-endpoint opt-in attribute: unlike the
// role policies (which vary correctly per endpoint), this gate must apply
// UNIFORMLY to every endpoint regardless of which AuthPolicies tier it
// carries, so a path allowlist is the simplest correct shape — mirrors the
// literal-path catch-alls already used for /api/{**rest} and /health/{**rest}.
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    // Exact request paths reachable while a password change is pending:
    //   - change-password: the actual escape hatch (the SPA's first-login
    //     screen and AccountPage's regular change-password form share this
    //     one endpoint; ChangeOwnPasswordAsync clears the flag on success).
    //   - logout: a gated user must always be able to end the session rather
    //     than being stuck.
    // Nothing else — not even GET /me — needs to be reachable: the SPA
    // decodes email/role straight from the JWT it already holds (claims.ts),
    // so the first-login screen needs no additional API call.
    private static readonly HashSet<string> AllowedPaths =
    [
        "/api/v1/auth/change-password",
        "/api/v1/auth/logout",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst("must_change_password")?.Value == "true"
            && !AllowedPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Auth.MustChangePassword",
                Detail = "A password change is required before this action is available.",
                Status = StatusCodes.Status403Forbidden,
            });
            return;
        }

        await next(context);
    }
}
