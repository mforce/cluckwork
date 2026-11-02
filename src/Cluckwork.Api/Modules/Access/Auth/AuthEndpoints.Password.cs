using Cluckwork.Api.Validation;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using FluentValidation;

namespace Cluckwork.Api.Modules.Access.Auth;

public static partial class AuthEndpoints
{
    private static async Task<IResult> ChangePassword(
        ChangeOwnPasswordRequest request, IAccessModule access,
        IValidator<ChangeOwnPasswordCommand> validator, ICurrentUser currentUser,
        TenantContext tenant, HttpResponse response, IWebHostEnvironment env,
        CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var command = new ChangeOwnPasswordCommand(request.CurrentPassword, request.NewPassword, request.Code);
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        // The user id comes from the token, never the request: a caller can only
        // ever change their OWN password here.
        var result = await access.ChangeOwnPasswordAsync(command, tenant.AccountId, currentUser.UserId, ct);
        if (!result.IsSuccess)
            // #1274 decision 12: 400, never 401, which would make the SPA
            // refresh and send the code again; a spent daily budget is 429.
            // Users.Conflict stays 400 here.
            return result.Error is RetryLaterError retry
                ? RetryLater(response, retry)
                : Results.Problem(result.Error.Description, statusCode: 400, title: result.Error.Code);

        // Every prior session was revoked; hand this device a fresh cookie + access
        // token so the one that made the change stays signed in.
        // #532 — the graph found this endpoint rewrote the shared cookie with no
        // account expectation at all; it was never part of #547. It now writes
        // the cookie for the CURRENT USER's account — the only farm it can
        // affect — so a change-password in farm A can no longer overwrite
        // farm B's cookie. The account is the handler's own return value
        // (ChangeOwnPasswordHandler returns it), not an ICurrentUser property:
        // the interface is the authorization input, and adding account
        // resolution to it would widen that surface for no gain.
        AuthCookies.SetRefreshCookie(response, result.Value.AccountId, result.Value.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
    }

    // #308 — mints the step-up grant. Non-enumerating on failure: a wrong
    // password is fine to say plainly (the caller is already proven to be a
    // specific authenticated user re-confirming their OWN credential — see
    // the class-level threat model on StepUpGrantService).
    private static async Task<IResult> StepUp(
        StepUpRequest request, IAccessModule access, IValidator<StepUpRequest> validator,
        ICurrentUser currentUser, TenantContext tenant, HttpResponse response, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationResponse.Problem(validation);

        var result = await access.IssueStepUpGrantAsync(
            tenant.AccountId, currentUser.UserId, request.Password, request.Code, ct);
        if (result.Error is RetryLaterError retry)
            return RetryLater(response, retry);
        if (result.Error is { Code: StepUpErrorCodes.Retry })
        {
            // #1274 decision 11: the step could not be saved, so no grant.
            response.Headers.RetryAfter = "30";
            return Results.Problem(result.Error.Description, statusCode: 503, title: result.Error.Code);
        }
        if (!result.IsSuccess)
            // #336 review — 400, exactly as ChangePassword above returns the SAME
            // Users.CurrentPasswordIncorrect error. A rejected step-up password is a
            // credential rejection, NOT an expired session, and it must not be
            // reported as one: the SPA's apiFetch treats every 401 as a stale access
            // token, silently refreshes, and REPLAYS the identical request. One typed
            // password would then cost TWO failed accesses, cutting the #128 five-
            // attempt account lockout this endpoint just gained to three — while
            // burning a refresh-token rotation per attempt, and signing the user out
            // mid-flow whenever the refresh itself is unavailable. Only the genuinely
            // unauthenticated branch above stays 401.
            return Results.Problem(
                result.Error.Description,
                statusCode: StatusCodes.Status400BadRequest,
                title: result.Error.Code);

        return Results.Ok(new StepUpResponse(result.Value.Token, result.Value.ExpiresAt));
    }
}
