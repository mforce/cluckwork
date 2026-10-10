using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.ChangeOwnPassword;

public sealed class ChangeOwnPasswordHandler(IIdentityProvider identity)
{
    public Task<Result<TokenPair>> HandleAsync(
        ChangeOwnPasswordCommand command, Guid userId, CancellationToken ct) =>
        identity.ChangeOwnPasswordAsync(userId, command.CurrentPassword, command.NewPassword, ct);
}
