namespace Cluckwork.Application.Features.Users.CreateUser;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Common;

// #103 — users are created with any assignable role (spec §5.1), or as a
// plain worker ("Worker" = no role row at all).
//
// #308 — creating another OWNER multiplies durable account control past a
// stolen access token's own lifetime (that token alone could otherwise mint
// a second, independent Owner), so it additionally requires a valid, recent
// step-up grant (see StepUpGrantService for the full threat model). Creating
// any OTHER role is ordinary farm administration and stays ungated — issue
// #308 explicitly calls out avoiding a blanket prompt.
public sealed class CreateUserHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var role = command.Role == CreateUserValidator.WorkerRole ? null : command.Role;

        if (role == Cluckwork.Domain.Accounts.Roles.Owner)
        {
            var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
            if (!proof.IsSuccess) return Result.Failure<Guid>(proof.Error);
        }

        return await identity.CreateUserAsync(
            accountId, command.Email.Trim(), command.Password,
            role: role,
            name: UserName.Normalize(command.Name), ct);
    }
}
