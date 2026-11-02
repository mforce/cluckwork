using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.ChangeOwnPassword;

public sealed class ChangeOwnPasswordHandler(IIdentityProvider identity)
{
    public Task<Result<TokenPair>> HandleAsync(
        ChangeOwnPasswordCommand command, Guid accountId, Guid userId, CancellationToken ct) =>
        identity.ChangeOwnPasswordAsync(accountId, userId, command.CurrentPassword, command.NewPassword, command.Code, ct);
}
