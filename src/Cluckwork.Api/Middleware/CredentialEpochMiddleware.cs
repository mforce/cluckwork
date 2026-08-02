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
            && context.Request.Path != LogoutPath)
        {
            var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var accountIdClaim = context.User.FindFirst("account_id")?.Value;
            var epochClaim = context.User.FindFirst("credential_epoch")?.Value;
            var tokenEpoch = int.TryParse(
                epochClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEpoch)
                ? parsedEpoch
                : 0;

            var currentEpoch = Guid.TryParse(userIdClaim, out var userId)
                && Guid.TryParse(accountIdClaim, out var accountId)
                ? await db.Users.AsNoTracking()
                    .Where(user => user.Id == userId && user.AccountId == accountId)
                    .Select(user => (int?)user.CredentialEpoch)
                    .SingleOrDefaultAsync(context.RequestAborted)
                : null;

            if (currentEpoch != tokenEpoch)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Auth.CredentialsSuperseded",
                    Detail = "Your credentials have been superseded. Sign in again.",
                    Status = StatusCodes.Status401Unauthorized,
                });
                return;
            }
        }

        await next(context);
    }
}
