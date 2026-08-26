namespace Cluckwork.Application.Features.Users.ChangeUserRole;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Domain.Common;

// #355/#360 — every role change mutates a durable authorization set. The role
// capability sets are not totally ordered (Worker and Sales grant different
// operations), and classifying old/new privilege would add a racy read before
// IdentityProvider's locked transaction. Use one request-only rule instead:
// every role change, including a no-op or apparent demotion, needs a fresh,
// single-use step-up grant before identity is touched.
public sealed class ChangeUserRoleHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        ChangeUserRoleCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        var role = command.Role == CreateUserValidator.WorkerRole ? null : command.Role;

        return await identity.ChangeUserRoleAsync(
            accountId, command.UserId, role, actingUserId, ct);
    }
}
