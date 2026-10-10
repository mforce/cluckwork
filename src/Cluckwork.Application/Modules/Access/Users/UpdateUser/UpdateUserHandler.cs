using Cluckwork.Application.Modules.Access.Contracts;

namespace Cluckwork.Application.Modules.Access.Users.UpdateUser;

// #163 — the update is scoped to the caller's account inside the provider, so a
// user id from another tenant resolves to NotFound, never a cross-account edit.
public sealed class UpdateUserHandler(IIdentityProvider identity)
{
    public Task<Result> HandleAsync(
        UpdateUserCommand command, Guid accountId, CancellationToken ct) =>
        identity.UpdateUserAsync(accountId, command.UserId, UserName.Normalize(command.Name), ct);
}
