using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Common;
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
        if (!currentUser.IsResolved || !tenant.IsResolved || ActingEpoch(user) is not { } epoch)
            return Results.Unauthorized();

        var result = await access.TurnOffTwoFactorAsync(tenant.AccountId, currentUser.UserId, epoch, stepUpToken, ct);
        if (!result.IsSuccess) return StepUpRouteProblem(result.Error);
        if (result.Value is not { } pair) return Results.NoContent();

        AuthCookies.SetRefreshCookie(response, pair.AccountId, pair.RefreshToken, access.RefreshTokenLifetimeDays, CookieSecure(env));
        return Results.Ok(new AccessTokenResponse(pair.AccessToken, pair.AccessTokenExpiry));
    }

    // #1277 — whether two-factor is on and how many recovery codes are left.
    // Never a code.
    private static async Task<IResult> TwoFactorStatus(
        IAccessModule access, ICurrentUser currentUser, TenantContext tenant, CancellationToken ct)
    {
        if (!currentUser.IsResolved || !tenant.IsResolved) return Results.Unauthorized();

        var status = await access.GetTwoFactorStatusAsync(tenant.AccountId, currentUser.UserId, ct);
        return Results.Ok(new TwoFactorStatusResponse(status.Enabled, status.RecoveryCodesLeft));
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
        if (result.Error is { Code: EnrolmentErrorCodes.NotEnabled })
            return Results.Problem(result.Error.Description, statusCode: StatusCodes.Status409Conflict, title: result.Error.Code);
        return result.IsSuccess ? Results.Ok(new RecoveryCodesResponse(result.Value)) : StepUpRouteProblem(result.Error);
    }

    private static int? ActingEpoch(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst("credential_epoch")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : null;

    // A missing or spent grant is 403, as on the Users routes. The other
    // failure is Auth.CredentialsSuperseded: the session went stale under the
    // request, and answers as CredentialEpochMiddleware does.
    private static IResult StepUpRouteProblem(Error error) =>
        Results.Problem(error.Description, title: error.Code,
            statusCode: error.Code == StepUpErrorCodes.Required
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized);
}

public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft);

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
