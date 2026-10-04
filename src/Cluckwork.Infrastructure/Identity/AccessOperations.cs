using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.Identity;

public sealed class AccessOperations(
    FirstRunAdminService firstRunAdmin,
    AdminRecoveryService recovery,
    AccountProvisioner provisioner) : IAccessOperations
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
}
