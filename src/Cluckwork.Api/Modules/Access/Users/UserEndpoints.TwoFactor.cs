using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Cluckwork.Api.Modules.Access.Users;

public static partial class UserEndpoints
{
    private static void MapFactorResets(RouteGroupBuilder group)
    {
        // #1280 — an Owner resets another user's two-factor (#1274 decision 17).
        group.MapDelete("/{id:guid}/two-factor", ResetUserTwoFactor)
            // Binds no body; caps what IdempotencyMiddleware buffers to hash it.
            .WithMaxRequestBodyBytes(512)
            .WithName("ResetUserTwoFactor")
            .WithSummary("Reset a user's two-factor so they enrol again. Signs an enrolled user out everywhere.");

        // #1284 — an Owner removes another user's passkeys (record 1282
        // decision 10). Inside IdempotentUnit, as the two-factor reset is.
        group.MapDelete("/{id:guid}/passkeys", RemoveUserPasskeys)
            // Binds no body; caps what IdempotencyMiddleware buffers to hash it.
            .WithMaxRequestBodyBytes(512)
            .WithName("RemoveUserPasskeys")
            .WithSummary("Remove every passkey of a user. Signs a user who had any out everywhere.");
    }

    private static async Task<IResult> ResetUserTwoFactor(
        Guid id,
        [FromHeader(Name = Cluckwork.Api.Modules.Access.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        IAccessModule access,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        // Same shape as CannotDisableSelf. An Owner replaces their own
        // authenticator through enrolment, and #1281 forbids an Owner to
        // turn two-factor off.
        if (currentUser.UserId == id)
            return Results.Problem(
                "You cannot reset your own two-factor. Ask another Owner.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotResetOwnTwoFactor");

        return MapUserStateResult(
            await access.ResetUserTwoFactorAsync(new ResetUserTwoFactorCommand(id, stepUpToken),
                tenant.AccountId, currentUser.UserId, ct));
    }

    // #1284 — an Owner manages their own passkeys on the Account page, so
    // their own id is refused before the grant is spent.
    private static async Task<IResult> RemoveUserPasskeys(
        Guid id,
        [FromHeader(Name = Cluckwork.Api.Modules.Access.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
        IAccessModule access,
        TenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!tenant.IsResolved || !currentUser.IsResolved) return Results.Unauthorized();

        if (currentUser.UserId == id)
            return Results.Problem(
                "You cannot remove your own passkeys here. Remove them from your account's security page.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Users.CannotRemoveOwnPasskeys");

        return MapUserStateResult(
            await access.RemoveUserPasskeysAsync(new RemoveUserPasskeysCommand(id, stepUpToken),
                tenant.AccountId, currentUser.UserId, ct));
    }
}
