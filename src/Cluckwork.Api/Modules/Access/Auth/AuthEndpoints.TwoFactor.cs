using System.Globalization;
using System.Security.Claims;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Cluckwork.Api.Modules.Access.Auth;

public static partial class AuthEndpoints
{
    // #1280 — the user turns their own two-factor off. 200 with a fresh pair
    // when it was on, because every other session ended; 204 otherwise.
    private static async Task<IResult> TurnOffTwoFactor(
        [FromHeader(Name = StepUpHeaderName)] string? stepUpToken,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved
            || !int.TryParse(user.FindFirst("credential_epoch")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            return Results.Unauthorized();

        var result = await access.TurnOffTwoFactorAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, ct);
        // The other failure is Auth.CredentialsSuperseded: the session went
        // stale under the request, and answers as CredentialEpochMiddleware does.
        if (!result.IsSuccess)
            return Results.Problem(result.Error.Description, title: result.Error.Code,
                statusCode: result.Error.Code == StepUpErrorCodes.Required
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status401Unauthorized);
        if (result.Value is not { } pair) return Results.NoContent();

        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }
}
