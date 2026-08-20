namespace Cluckwork.Api.Middleware;

using System.Security.Claims;

// #532 — /auth/login is AllowAnonymous, but AllowAnonymous does NOT stop
// UseAuthentication from populating context.User when a bearer happens to be
// present. Three middlewares downstream then read that principal, and once
// login takes a farm code they all read it for the WRONG farm:
//
//   * TenantResolutionMiddleware resolves TenantContext from the bearer's
//     account_id claim, which arms #546's write-side guard. A farm-A bearer
//     posting a farm-B login makes AccessFailedAsync write an AspNetUsers row
//     whose AccountId is B while the resolved tenant is A, so
//     TenantStampInterceptor throws TenantWriteMismatchException — an exception
//     nothing in src/ catches. The request 500s and, critically,
//     AccessFailedCount NEVER INCREMENTS: the #128 account lockout is bypassed,
//     giving unlimited password guessing against any other farm's users. That
//     bypass is the defect this type exists to close.
//   * CredentialEpochMiddleware rejects a stale, superseded or disabled bearer
//     before the handler runs, so one tab's expired session would break a
//     perfectly good login for a different farm in another tab.
//   * MustChangePasswordMiddleware 403s everything outside its two-path
//     allowlist, which does not include login.
//
// CLEARING the principal, rather than teaching each of those three to honour a
// marker, is what makes login behave IDENTICALLY with and without a bearer. A
// marker honoured by only one of them is the version that looks right and is
// not: two reviewers independently pointed out that a TenantResolutionMiddleware-
// only opt-out leaves the other two rejecting the request before the handler.
//
// Runs immediately after UseAuthentication and before TenantResolutionMiddleware.
// Routing has already run by that point (see the #309 body-limit comment in
// Program.cs), so endpoint metadata is available here.
public sealed class AmbientPrincipalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata
                .GetMetadata<IgnoresAmbientPrincipalAttribute>() is not null)
        {
            // An empty, UNAUTHENTICATED principal — the same shape the pipeline
            // would carry had no Authorization header been sent at all. Not
            // null: downstream code reads context.User.Identity?.IsAuthenticated
            // without null-checking the principal itself.
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        await next(context);
    }
}

// Marks an endpoint that must ignore any bearer the caller happened to send.
// Deliberately a marker rather than AllowAnonymous: AllowAnonymous governs
// AUTHORIZATION (may this request proceed unauthenticated) and leaves the
// authenticated principal in place, which is exactly the problem. Mirrors
// ReadsRequestBodyAttribute's shape.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class IgnoresAmbientPrincipalAttribute : Attribute;
