namespace Cluckwork.Application.Features.Users.ChangeOwnPassword;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class ChangeOwnPasswordHandler(IIdentityProvider identity)
{
    public Task<Result<TokenPair>> HandleAsync(
        ChangeOwnPasswordCommand command, Guid userId, CancellationToken ct) =>
        identity.ChangeOwnPasswordAsync(userId, command.CurrentPassword, command.NewPassword, ct);
}
