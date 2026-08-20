namespace Cluckwork.Api.Middleware;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// #364 — server-side access-token revocation. Every authenticated request is
// bound to the credential epoch held by its exact (user, account) row. Missing
// and malformed claims deliberately become retired epoch zero, never an opt-out.
public sealed class CredentialEpochMiddleware(RequestDelegate next)
{
    private const string LogoutPath = "/api/v1/auth/logout";

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.Features.Get<IExceptionHandlerFeature>() is null
            && !IsLogoutPath(context.Request.Path))
        {
            var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var accountIdClaim = context.User.FindFirst("account_id")?.Value;
            var epochClaim = context.User.FindFirst("credential_epoch")?.Value;
            var tokenEpoch = int.TryParse(
                epochClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEpoch)
                ? parsedEpoch
                : 0;

            // #532 — Account.IsActive folds into the EXISTING per-request read as
            // a correlated subquery: one round trip, not two. This is what makes
            // suspension immediate rather than "effective at token expiry"
            // (epic #530 decision 15), so it is enforcement, not a nicety — do
            // not delete it as cosmetic.
            //
            // IgnoreQueryFilters is DEFENSIVE, not required: it makes this read
            // independent of TenantResolutionMiddleware having run. Today
            // nothing depends on that — TenantResolutionMiddleware resolves the
            // tenant from the SAME account_id claim, so on any request that
            // reaches the subquery tenant.AccountId == accountId and the
            // filter matches, and an unparseable claim means Guid.TryParse above
            // is false and the subquery is never built. Keep it anyway: a read
            // whose correctness does not hinge on middleware order is worth
            // keeping. No test claims to cover this.
            var credentialState = Guid.TryParse(userIdClaim, out var userId)
                && Guid.TryParse(accountIdClaim, out var accountId)
                ? await db.Users.AsNoTracking()
                    .Where(user => user.Id == userId && user.AccountId == accountId)
                    .Select(user => new
                    {
                        user.CredentialEpoch,
                        user.DisabledAt,
                        AccountIsActive = db.Accounts.IgnoreQueryFilters()
                            .Where(account => account.Id == user.AccountId)
                            .Select(account => (bool?)account.IsActive)
                            .FirstOrDefault(),
                    })
                    .SingleOrDefaultAsync(context.RequestAborted)
                : null;

            // #532 — PRECEDENCE IS DELIBERATE, and it is the reason the epoch
            // test moved to last. Suspending a farm bumps every one of its users'
            // CredentialEpoch, so a suspended farm's bearer fails BOTH the
            // account test and the epoch test. Checking the epoch first would
            // answer Auth.CredentialsSuperseded — "sign in again" — to someone
            // whose farm is suspended and whose sign-in cannot succeed. Order:
            // unknown user, then disabled user, then suspended farm, then epoch.
            if (credentialState is null
                || credentialState.DisabledAt is not null
                || credentialState.AccountIsActive != true
                || credentialState.CredentialEpoch != tokenEpoch)
            {
                var disabled = credentialState?.DisabledAt is not null;
                // DisabledAt is null here by construction: farmSuspended is only
                // read in the disabled ? … : farmSuspended ? … ternaries below,
                // where the disabled branch already failed, so the DisabledAt
                // clause is unreachable and has been deleted.
                var farmSuspended = credentialState is not null
                    && credentialState.AccountIsActive != true;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = disabled
                        ? "Auth.AccountDisabled"
                        : farmSuspended
                            ? "Auth.FarmSuspended"
                            : "Auth.CredentialsSuperseded",
                    Detail = disabled
                        ? "Your account has been disabled."
                        : farmSuspended
                            ? "This farm is suspended. Contact your administrator."
                            : "Your credentials have been superseded. Sign in again.",
                    Status = StatusCodes.Status401Unauthorized,
                });
                return;
            }
        }

        await next(context);
    }

    // Endpoint routing accepts the conventional trailing-slash form too. Keep
    // both spellings reachable for a superseded bearer, without widening the
    // exemption to descendants such as /auth/logout/anything.
    private static bool IsLogoutPath(PathString path) =>
        path == LogoutPath || path == $"{LogoutPath}/";
}
