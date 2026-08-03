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

            var credentialState = Guid.TryParse(userIdClaim, out var userId)
                && Guid.TryParse(accountIdClaim, out var accountId)
                ? await db.Users.AsNoTracking()
                    .Where(user => user.Id == userId && user.AccountId == accountId)
                    .Select(user => new { user.CredentialEpoch, user.DisabledAt })
                    .SingleOrDefaultAsync(context.RequestAborted)
                : null;

            if (credentialState is null
                || credentialState.CredentialEpoch != tokenEpoch
                || credentialState.DisabledAt is not null)
            {
                var disabled = credentialState?.DisabledAt is not null;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = disabled ? "Auth.AccountDisabled" : "Auth.CredentialsSuperseded",
                    Detail = disabled
                        ? "Your account has been disabled."
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
