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
    // concurrent. What does conflict: the currency change's FOR UPDATE (the
    // point), and any plain UPDATE of the account row (FOR NO KEY UPDATE) —
    // so a non-currency settings save can briefly wait behind in-flight
    // money writes. Accepted: that path is rare, admin-only, and the waits
    // are transaction-length. MUST be called inside an open transaction — on
    // autocommit the lock evaporates with the statement and guards nothing.
    Task<Account?> GetCurrentSharedLockedAsync(CancellationToken ct = default);

    // The currency change's half: FOR UPDATE, tracked. Holding it means no
    // money writer is mid-flight (their FOR SHARE would have blocked us) and
    // none can start until we commit — so one probe after this read is
    // authoritative. Same transaction requirement as the shared variant.
    // NOTE: when the account is already tracked, identity resolution returns
    // the EXISTING instance without refreshing its values — this call takes
    // the lock, it does not give you fresh data. Version's concurrency token
    // is what guards stale writes.
    Task<Account?> GetCurrentLockedAsync(CancellationToken ct = default);

    // #532 — farm-code login. Resolves an account by its slug with NO ambient
    // tenant, and that is the whole difficulty: /auth/login is AllowAnonymous,
    // so TenantContext is unresolved and the Account query filter
    // (AccountId == tenant.AccountId, i.e. Guid.Empty) matches ZERO rows. A
    // lookup written the obvious way therefore reports EVERY farm code as
    // unknown, and does so silently — the implementation must IgnoreQueryFilters.
    //
    // The slug is normalized INSIDE the implementation rather than by the
    // caller: stored slugs are guaranteed lowercase (Account.ValidateSlug trims
    // but REJECTS uppercase rather than folding it), so a caller that forgets
    // to fold turns a phone keyboard's auto-capital into "unknown farm code".
    // One place, impossible to forget.
    Task<Account?> FindBySlugAsync(string slug, CancellationToken ct = default);

    // Put a tracked account back the way the database has it. A rolled-back
    // transaction undoes the row but not the in-memory entity, and something
    // else saves through this context later in the same request (the
    // idempotency record) — which would flush the abandoned edit.
    void DiscardChanges(Account account);
}
