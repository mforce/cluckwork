namespace Cluckwork.Application.Features.Accounts;

using Cluckwork.Domain.Accounts;

public interface IAccountRepository
{
    // The current tenant's account (query-filter scoped). Untracked — every
    // caller so far only reads currency/timezone off it.
    Task<Account?> GetCurrentAsync(CancellationToken ct = default);

    // Same row, tracked, for the settings write (#123). Kept separate so the
    // hot read paths (IFarmClock on every dated request) stay untracked.
    // Tracked, NOT locked — there is no FOR UPDATE here. #162 is where the row
    // lock belongs, and naming this "ForUpdate" implied one it never took.
    Task<Account?> GetCurrentTrackedAsync(CancellationToken ct = default);

    // #162 §4.6 — the money-writer's half of the currency-lock protocol: FOR
    // SHARE on the account row, taken by EVERY handler that stamps
    // DefaultCurrencyCode onto a new row, inside the same transaction as its
    // insert. Shared locks never block each other, so money writes stay
    // concurrent; only the currency change's FOR UPDATE conflicts. MUST be
    // called inside an open transaction — on autocommit the lock evaporates
    // with the statement and guards nothing.
    Task<Account?> GetCurrentSharedLockedAsync(CancellationToken ct = default);

    // The currency change's half: FOR UPDATE, tracked. Holding it means no
    // money writer is mid-flight (their FOR SHARE would have blocked us) and
    // none can start until we commit — so one probe after this read is
    // authoritative. Same transaction requirement as the shared variant.
    Task<Account?> GetCurrentLockedAsync(CancellationToken ct = default);

    // Put a tracked account back the way the database has it. A rolled-back
    // transaction undoes the row but not the in-memory entity, and something
    // else saves through this context later in the same request (the
    // idempotency record) — which would flush the abandoned edit.
    void DiscardChanges(Account account);
}
