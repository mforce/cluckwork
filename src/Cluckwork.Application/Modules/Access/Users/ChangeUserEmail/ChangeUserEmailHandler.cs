using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.ChangeUserEmail;

public sealed class ChangeUserEmailHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        ChangeUserEmailCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;
        return await identity.ChangeUserEmailAsync(
            accountId, command.UserId, command.Email.Trim(), actingUserId, ct);
    }
}
