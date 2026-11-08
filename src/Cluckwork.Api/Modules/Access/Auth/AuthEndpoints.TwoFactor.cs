using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Cluckwork.Api.Configuration;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();
        // #1281 — every Owner must use two-factor, so the refusal comes before
        // the grant is spent. The role claim is current: a role change bumps
        // the epoch, which turn-off compares under the row lock.
        if (user.IsInRole(Roles.Owner))
            return Results.Problem("An Owner cannot turn off two-factor authentication. Replace your authenticator instead.",
                statusCode: StatusCodes.Status403Forbidden, title: EnrolmentErrorCodes.OwnerCannotTurnOff);

        var result = await access.TurnOffTwoFactorAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, ct);
        if (!result.IsSuccess) return StepUpRouteProblem(result.Error);
        if (result.Value is not { } pair) return Results.NoContent();

        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }

    // #1277 — whether two-factor is on and how many recovery codes are left.
    // Never a code. #1281 — an Owner also gets the grace deadline; nobody else
    // must enrol, so nobody else gets one.
    private static async Task<IResult> TwoFactorStatus(
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user,
        IOptions<OwnerGraceOptions> ownerGrace, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var status = await access.GetTwoFactorStatusAsync(tenant.AccountId, currentUser.UserId, ct);
        var requiredBy = user.IsInRole(Roles.Owner) ? status.RequiredSince?.AddDays(ownerGrace.Value.OwnerGraceDays) : null;
        return Results.Ok(new TwoFactorStatusResponse(status.Enabled, status.RecoveryCodesLeft, requiredBy));
    }

    // #1277 — a new key for a first enrolment or a replacement. The live key
    // keeps working until confirm.
    private static async Task<IResult> StartTwoFactorEnrolment(
        [FromHeader(Name = StepUpHeaderName)] string? stepUpToken,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();

        var result = await access.StartTwoFactorEnrolmentAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, ct);
        return result.IsSuccess
            ? Results.Ok(new EnrolmentStartResponse(result.Value.Key, result.Value.OtpauthUri))
            : StepUpRouteProblem(result.Error);
    }

    // #1277 — the code from the new key turns two-factor on or swaps the key,
    // and ends every other session and connected app. 400 keeps a wrong
    // confirm from looking like an expired session to the SPA (decision 12).
    private static async Task<IResult> ConfirmTwoFactorEnrolment(
        ConfirmEnrolmentRequest request, IAccessModule access, ICurrentUser currentUser, TenantContext tenant,
        HttpResponse response, IWebHostEnvironment env, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var result = await access.ConfirmTwoFactorEnrolmentAsync(tenant.AccountId, currentUser.UserId, request.Code, ct);
        if (result.Error is EnrolmentCodeIncorrectError incorrect)
            return Results.Problem(incorrect.Description, statusCode: StatusCodes.Status400BadRequest, title: incorrect.Code,
                extensions: incorrect.AttemptsLeft is { } left
                    ? new Dictionary<string, object?> { ["attemptsLeft"] = left }
                    : null);
        if (!result.IsSuccess)
            return Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code);

        var pair = result.Value.Tokens;
        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new EnrolmentConfirmedResponse(pair.AccessToken, pair.AccessTokenExpiry, result.Value.RecoveryCodes));
    }

    // #1277 — ten new recovery codes; the old ones stop working. No session ends.
    private static async Task<IResult> RegenerateRecoveryCodes(
        [FromHeader(Name = StepUpHeaderName)] string? stepUpToken,
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();

        var result = await access.RegenerateRecoveryCodesAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, ct);
        return result.IsSuccess ? Results.Ok(new RecoveryCodesResponse(result.Value)) : StepUpRouteProblem(result.Error);
    }

    private static int? ActingEpoch(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst("credential_epoch")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : null;

    // The refusals of the routes that spend a step-up grant. A missing or spent
    // grant is 403, as on the Users routes. Only a session that went stale
    // under the request is 401, as CredentialEpochMiddleware answers it,
    // because the SPA refreshes and resends on a 401. Anything else is a bug,
    // and reaches the exception handler.
    internal static IResult StepUpRouteProblem(Error error) =>
        Results.Problem(error.Description, title: error.Code, statusCode: error.Code switch
        {
            StepUpErrorCodes.Required => StatusCodes.Status403Forbidden,
            CredentialErrorCodes.Superseded => StatusCodes.Status401Unauthorized,
            EnrolmentErrorCodes.NotEnabled => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException($"No answer is mapped for {error.Code}."),
        });
}

public sealed record TwoFactorStatusResponse(
    bool Enabled, int RecoveryCodesLeft,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RequiredBy);

// The key and the URI are credentials, so the record never prints them.
public sealed record EnrolmentStartResponse(string Key, string OtpauthUri)
{
    public override string ToString() => "[redacted]";
}

public sealed record ConfirmEnrolmentRequest(string? Code)
{
    public override string ToString() => "[redacted]";
}

// RecoveryCodes appears on a first enrolment only.
public sealed record EnrolmentConfirmedResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RecoveryCodes)
{
    public override string ToString() => "[redacted]";
}

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes)
{
    public override string ToString() => "[redacted]";
}
