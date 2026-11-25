namespace Cluckwork.Application.Modules.Access.Contracts;

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

    // #1284 (record 1282 decision 10) — the clear-passkeys verb, one farm per
    // call, in its own scope and transaction: every user of the farm who has a
    // passkey loses them all, their sessions and connected apps end, and each
    // gets one User.PasskeysCleared row. Returns how many users it cleared.
    Task<int> ClearPasskeysAsync(Guid accountId, string? reason, CancellationToken ct);

    Task<Result<Guid>> CreateUserAsync(
        Guid accountId, string email, string password, string? role, string? name, CancellationToken ct);
}
