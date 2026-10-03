using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Cluckwork.Application.Features.Users;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cluckwork.Api.Middleware;

// #364 — server-side access-token revocation. Every authenticated request is
// bound to the credential epoch held by its exact (user, account) row. Missing
// and malformed claims deliberately become retired epoch zero, never an opt-out.
// That holds only while the stored epoch is at least 1, which nothing enforces
// yet (#1031). Since #857 the read itself is ICredentialEpochVerifier's; this
// middleware keeps the claims, the exemptions and the responses.
public sealed class CredentialEpochMiddleware(RequestDelegate next)
{
    private const string LogoutPath = "/api/v1/auth/logout";

    public async Task InvokeAsync(HttpContext context, ICredentialEpochVerifier verifier)
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

            // An unparseable user or account claim never reaches the database.
            var verdict = Guid.TryParse(userIdClaim, out var userId)
                && Guid.TryParse(accountIdClaim, out var accountId)
                ? await verifier.VerifyAsync(userId, accountId, tokenEpoch, context.RequestAborted)
                : CredentialVerdict.UnknownUser;

            if (verdict != CredentialVerdict.Current)
            {
                var (title, detail) = verdict switch
                {
                    CredentialVerdict.Disabled =>
                        ("Auth.AccountDisabled", "Your account has been disabled."),
                    CredentialVerdict.FarmSuspended =>
                        ("Auth.FarmSuspended", "This farm is suspended. Contact your administrator."),
                    _ => ("Auth.CredentialsSuperseded", "Your credentials have been superseded. Sign in again."),
                };
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = title,
                    Detail = detail,
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
