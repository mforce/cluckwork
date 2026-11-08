using Cluckwork.Api.Hosting;
using Cluckwork.Api.Modules.Access.OAuth;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Modules.Access.OAuth;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace Cluckwork.Api.Middleware;

// #364 — server-side access-token revocation. Every authenticated request is
// bound to the credential epoch held by its exact (user, account) row. Missing
// and malformed claims deliberately become retired epoch zero, never an opt-out:
// the verifier never matches a stored epoch below 1, and a CHECK keeps such a
// row from being written at all (#1031). Since #857 the read itself is
// ICredentialEpochVerifier's; this middleware keeps the claims, the exemptions
// and the responses.
public sealed class CredentialEpochMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICredentialEpochVerifier verifier)
    {
        if (ChecksRequest(context))
        {
            var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var accountIdClaim = context.User.FindFirst("account_id")?.Value;
            var epochClaim = context.User.FindFirst("credential_epoch")?.Value;
            var tokenEpoch = int.TryParse(
                epochClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEpoch)
                ? parsedEpoch
                : 0;

            // An unparseable user or account claim never reaches the database.
            var check = Guid.TryParse(userIdClaim, out var userId)
                && Guid.TryParse(accountIdClaim, out var accountId)
                ? await verifier.VerifyAsync(userId, accountId, tokenEpoch,
                    // #800 — only an OAuth access token carries client_id.
                    context.User.HasClaim(claim => claim.Type == OpenIddictConstants.Claims.ClientId),
                    context.RequestAborted)
                : new CredentialCheck(CredentialVerdict.UnknownUser, false, null);
            var verdict = check.Verdict;

            // #1229 — an OAuth token names its authorization and itself. Only a Current
            // one counts as use, and a Superseded one can never pass again, so it is revoked.
            if (Guid.TryParse(context.User.GetAuthorizationId(), out var appAuthorizationId))
            {
                var apps = context.RequestServices.GetRequiredService<ConnectedApps>();
                if (verdict == CredentialVerdict.Current)
                    await apps.RecordUseAsync(appAuthorizationId, context.RequestAborted);
                else if (verdict == CredentialVerdict.Superseded
                    && Guid.TryParse(context.User.GetTokenId(), out var appTokenId))
                    await apps.RevokeSupersededTokenAsync(appTokenId, context.RequestAborted);
            }

            if (verdict != CredentialVerdict.Current)
            {
                var (title, detail) = verdict switch
                {
                    CredentialVerdict.Disabled =>
                        ("Auth.AccountDisabled", "Your account has been disabled."),
                    CredentialVerdict.FarmSuspended =>
                        (SecondFactorErrorCodes.FarmSuspended, "This farm is suspended. Contact your administrator."),
                    CredentialVerdict.ConnectedAppsOff =>
                        (OAuthEndpoints.ConnectedAppsOff, "This farm doesn't allow connected apps. Ask an Owner."),
                    _ => (CredentialErrorCodes.Superseded, "Your credentials have been superseded. Sign in again."),
                };
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                // #806 — the token no longer grants access, so an MCP client must re-authorize.
                CluckworkMcp.AddChallenge(context, "error=\"invalid_token\"");
                // The writer sets the content type itself, so it is passed here, not set before.
                await context.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = title,
                    Detail = detail,
                    Status = StatusCodes.Status401Unauthorized,
                }, options: null, contentType: "application/problem+json");
                return;
            }

            // #1281 — the Owner two-factor gate reads the row through this check.
            context.Features.Set<CredentialCheck?>(check);
        }

        await next(context);
    }

    // The requests this check reads the row for: authenticated, not the
    // exception handler's re-execution and not logout. The Owner two-factor
    // gate skips exactly the others, so it can require the check on these.
    internal static bool ChecksRequest(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && context.Features.Get<IExceptionHandlerFeature>() is null
        && !LogoutPath.Matches(context.Request.Path);
}
