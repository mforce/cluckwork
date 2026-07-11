namespace Cluckwork.Infrastructure.Persistence;

// Scoped per-request tenant resolution.
// Populated by middleware from the authenticated principal's account_id claim
// before any handler runs (tech spec §4.2).
public sealed class TenantContext
{
    public Guid AccountId { get; private set; }
    public bool IsResolved { get; private set; }

    public void Resolve(Guid accountId)
    {
        AccountId = accountId;
        IsResolved = true;
    }
}
