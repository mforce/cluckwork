namespace Cluckwork.Infrastructure.Persistence;

// Scoped per-request tenant resolution.
// Populated by middleware from the authenticated principal's account_id claim
// before any handler runs (tech spec §4.2).
//
// SINGLE-ASSIGNMENT (#546). Re-resolving the SAME account is a no-op; a
// DIFFERENT account throws. Re-pointing a live scope at another account is
// never legitimate: everything in the scope reads this lazily — the 27 EF
// global query filters, TenantStampInterceptor, IAuditWriter — so a second
// Resolve silently changes what already-executed code MEANT.
//
// The hazard becomes reachable with #532 (login by farm code): /auth/login is
// AllowAnonymous but can still be called carrying a valid Farm A bearer, which
// TenantResolutionMiddleware resolves before the handler runs. This shuts the
// door before that lands rather than relying on convention afterwards.
public sealed class TenantContext
{
    public Guid AccountId { get; private set; }
    public bool IsResolved { get; private set; }

    public void Resolve(Guid accountId)
    {
        if (IsResolved)
        {
            // Same account: a deliberate no-op, NOT an error. A caller that
            // resolves defensively must not thereby become order-dependent.
            if (AccountId == accountId) return;

            throw new TenantReassignmentException(AccountId, accountId);
        }

        AccountId = accountId;
        IsResolved = true;
    }
}
