namespace Cluckwork.Application.Features.Users.CreateUser;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Common;

// #103 — users are created with any assignable role (spec §5.1), or as a
// plain worker ("Worker" = no role row at all).
//
// #308/#360 — every interactive creation mints a reusable credential whose
// lifetime exceeds the caller's access token, regardless of the new user's
// role. A valid Owner bearer therefore is not sufficient on its own: every
// call additionally requires a fresh, single-use step-up grant. Trusted
// one-shot provisioning uses IIdentityProvider directly; it never selects a
// bypass through this command.
public sealed class CreateUserHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateUserCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return Result.Failure<Guid>(proof.Error);

        var role = command.Role == CreateUserValidator.WorkerRole ? null : command.Role;

        // #339 — `mustChangePassword` stays defaulted (false). The forced-change
        // gate exists for the `bootstrap-admin` temp password the system itself
        // generates; a user an Owner creates here already gets a password the
        // Owner chose, so it is not put through that gate.
        return await identity.CreateUserAsync(
            accountId, command.Email.Trim(), command.Password,
            role: role,
            name: UserName.Normalize(command.Name), ct: ct);
    }
}
