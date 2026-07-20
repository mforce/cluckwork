namespace Cluckwork.Application.Features.Users.CreateUser;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #103 — users are created with any assignable role (spec §5.1), or as a
// plain worker ("Worker" = no role row at all).
public sealed class CreateUserHandler(IIdentityProvider identity)
{
    public Task<Result<Guid>> HandleAsync(
        CreateUserCommand command, Guid accountId, CancellationToken ct) =>
        identity.CreateUserAsync(
            accountId, command.Email.Trim(), command.Password,
            role: command.Role == CreateUserValidator.WorkerRole ? null : command.Role, ct);
}
