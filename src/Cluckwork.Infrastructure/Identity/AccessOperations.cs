using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.Identity;

public sealed class AccessOperations(
    FirstRunAdminService firstRunAdmin,
    AdminRecoveryService recovery,
    AccountProvisioner provisioner,
    AccountSuspensionService suspension,
    AccountRenameService rename) : IAccessOperations
{
    public Task<Result<FirstRunAdminOutcome>> BootstrapAdminAsync(string? email, CancellationToken ct) =>
        firstRunAdmin.ProvisionAsync(email, ct);

    public Task<Result<AdminRecoveryResult>> RecoverAdminAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct) =>
        recovery.RecoverAsync(email, accountId, reason, ct);

    public async Task<Result<ProvisionedAccountDetails>> ProvisionAccountAsync(
        string? name, string? slug, string? ownerEmail,
        string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct)
    {
        var result = await provisioner.ProvisionAsync(name, slug, ownerEmail, locale, currencyCode, timeZoneId, ct);
        if (result.IsFailure)
            return Result.Failure<ProvisionedAccountDetails>(result.Error);
        var outcome = result.Value;
        return new ProvisionedAccountDetails(outcome.AccountId, outcome.Slug, outcome.OwnerEmail, outcome.TemporaryPassword);
    }

    public Task<Result<AccountLifecycleOutcome>> SuspendAccountAsync(
        Guid accountId, string? reason, CancellationToken ct) =>
        suspension.SuspendAsync(accountId, reason, ct);

    public Task<Result<AccountLifecycleOutcome>> ReactivateAccountAsync(
        Guid accountId, string? reason, CancellationToken ct) =>
        suspension.ReactivateAsync(accountId, reason, ct);

    public Task<Result<AccountRenameOutcome>> RenameAccountAsync(
        string currentSlug, string? newSlug, string? reason, CancellationToken ct) =>
        rename.RenameAsync(currentSlug, newSlug, reason, ct);
}
