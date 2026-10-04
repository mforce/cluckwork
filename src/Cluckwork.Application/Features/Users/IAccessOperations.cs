using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Features.Users;

// #857 — Access's surface for the operator verbs: one-shot commands whose only
// authority is shell access to the deployment. Each member forwards to the
// existing service, so validation, locks, audit rows and generated passwords
// are theirs, unchanged. HTTP adapters use IAccessModule instead.
public interface IAccessOperations
{
    Task<Result<FirstRunAdminOutcome>> BootstrapAdminAsync(string? email, CancellationToken ct);

    Task<Result<AdminRecoveryResult>> RecoverAdminAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct);

    Task<Result<AccountProvisionOutcome>> ProvisionAccountAsync(
        string? name, string? slug, string? ownerEmail,
        string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct);
}
