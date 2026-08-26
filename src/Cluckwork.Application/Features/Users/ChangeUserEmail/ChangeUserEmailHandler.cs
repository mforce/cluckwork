namespace Cluckwork.Application.Features.Users.ChangeUserEmail;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

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
