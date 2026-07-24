namespace Cluckwork.Application.Features.Users.SetUserPassword;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class SetUserPasswordHandler(IIdentityProvider identity)
{
    public Task<Result> HandleAsync(
        SetUserPasswordCommand command, Guid accountId, CancellationToken ct) =>
        identity.SetUserPasswordAsync(accountId, command.UserId, command.NewPassword, ct);
}
