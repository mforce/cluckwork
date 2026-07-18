namespace Cluckwork.Application.Features.Users.CreateUser;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// #73 — minimal second user so the admin gate is testable and useful. Workers
// carry no role; only the Admin role changes what the API allows.
public sealed class CreateUserHandler(IIdentityProvider identity)
{
    public Task<Result<Guid>> HandleAsync(
        CreateUserCommand command, Guid accountId, CancellationToken ct) =>
        identity.CreateUserAsync(
            accountId, command.Email.Trim(), command.Password,
            isAdmin: command.Role == CreateUserValidator.AdminRole, ct);
}
