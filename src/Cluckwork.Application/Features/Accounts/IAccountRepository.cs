namespace Cluckwork.Application.Features.Accounts;

using Cluckwork.Domain.Accounts;

public interface IAccountRepository
{
    // The current tenant's account (query-filter scoped). Untracked — every
    // caller so far only reads currency/timezone off it.
    Task<Account?> GetCurrentAsync(CancellationToken ct = default);

    // Same row, tracked, for the settings write (#123). Kept separate so the
    // hot read paths (IFarmClock on every dated request) stay untracked.
    Task<Account?> GetCurrentForUpdateAsync(CancellationToken ct = default);

    // Put a tracked account back the way the database has it. A rolled-back
    // transaction undoes the row but not the in-memory entity, and something
    // else saves through this context later in the same request (the
    // idempotency record) — which would flush the abandoned edit.
    void DiscardChanges(Account account);
}
