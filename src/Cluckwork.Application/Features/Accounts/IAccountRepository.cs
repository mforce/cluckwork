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
}
