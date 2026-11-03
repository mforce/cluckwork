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
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var result = await access.TurnOffTwoFactorAsync(tenant.AccountId, currentUser.UserId, stepUpToken, ct);
        if (!result.IsSuccess)
            return result.Error.Code == StepUpErrorCodes.Required
                ? Results.Problem(result.Error.Description, statusCode: StatusCodes.Status403Forbidden, title: result.Error.Code)
                : Results.NotFound();
        if (result.Value is not { } pair) return Results.NoContent();

        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }
}
