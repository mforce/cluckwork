namespace Cluckwork.Application.Features.Users.ChangeUserRole;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Domain.Common;

// #355 — promote/demote an existing user. Same step-up threat model as
// CreateUserHandler/SetUserPasswordHandler (#308): granting OWNER requires a
// fresh proof; every other target role is ordinary farm administration and
// stays ungated. This check is a PURE FUNCTION of the REQUESTED role, decided
// once here before touching identity — deliberately NOT re-checked inside the
// transaction. An Owner resubmitting an unchanged Owner role for a DIFFERENT
// already-Owner user is asked to re-prove even though IdentityProvider will
// no-op it — accepted friction (grilled and rejected the alternative: doing
// this safely needs step-up validated INSIDE the locked transaction, pushing
// step-up knowledge into a layer that today knows nothing about it, for a
// rare edge case). A caller can never self-target here at all — UserEndpoints
// refuses that at 400 before this handler ever runs.
public sealed class ChangeUserRoleHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        ChangeUserRoleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        if (command.Role == Cluckwork.Domain.Accounts.Roles.Owner)
        {
            var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
            if (!proof.IsSuccess) return proof;
        }

        var role = command.Role == CreateUserValidator.WorkerRole ? null : command.Role;

        return await identity.ChangeUserRoleAsync(accountId, command.UserId, role, actingUserId, ct);
    }
}
