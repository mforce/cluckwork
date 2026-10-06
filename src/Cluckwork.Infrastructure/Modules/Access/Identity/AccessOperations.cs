using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed class AccessOperations(
    FirstRunAdminService firstRunAdmin,
    AdminRecoveryService recovery,
    AccountProvisioner provisioner,
    IIdentityProvider identity) : IAccessOperations
{
    public Task<Result<FirstRunAdminOutcome>> BootstrapAdminAsync(string? email, CancellationToken ct) =>
        firstRunAdmin.ProvisionAsync(email, ct);

    public Task<Result<AdminRecoveryResult>> RecoverAdminAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct) =>
        recovery.RecoverAsync(email, accountId, reason, ct);

    public Task<Result<AccountProvisionOutcome>> ProvisionAccountAsync(
        string? name, string? slug, string? ownerEmail,
        string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct)
        => provisioner.ProvisionAsync(name, slug, ownerEmail, locale, currencyCode, timeZoneId, ct);

    public Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role, string? name, CancellationToken ct) =>
        identity.CreateUserAsync(accountId, email, password, role, name, mustChangePassword: false, ct: ct);
}
