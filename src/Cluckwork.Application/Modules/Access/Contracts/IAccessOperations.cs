using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Modules.Access.Contracts;

// #857 — Access's surface for the operator verbs: one-shot commands whose only
// authority is shell access to the deployment. Each member forwards to the
// existing service, so validation, locks, audit rows and generated passwords
// are theirs, unchanged. HTTP adapters use IAccessModule instead.
[ModuleContract("Access")]
public interface IAccessOperations
{
    Task<Result<FirstRunAdminOutcome>> BootstrapAdminAsync(string? email, CancellationToken ct);

    Task<Result<AdminRecoveryResult>> RecoverAdminAsync(
        string? email, Guid? accountId, string? reason, CancellationToken ct);

    Task<Result<AccountProvisionOutcome>> ProvisionAccountAsync(
        string? name, string? slug, string? ownerEmail,
        string? locale, string? currencyCode, string? timeZoneId, CancellationToken ct);

    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role, string? name, CancellationToken ct);
}
