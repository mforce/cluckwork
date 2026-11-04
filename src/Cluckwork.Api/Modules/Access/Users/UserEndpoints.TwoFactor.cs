using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Cluckwork.Api.Modules.Access.Users;

public static partial class UserEndpoints
{
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
}
