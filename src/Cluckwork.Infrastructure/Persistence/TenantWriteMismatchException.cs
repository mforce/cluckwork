namespace Cluckwork.Infrastructure.Persistence;

// Distinct type so a write-side tenant breach is greppable in logs rather than
// read as a generic 500 — the same rationale FarmTimeZoneException carries
// (Infrastructure/Time/FarmClock.cs).
//
// Thrown by TenantStampInterceptor when a tracked write carries an AccountId
// that is not the resolved tenant's. That is a bug or an attack, never user
// input, so it is an exception rather than a Result failure.
public sealed class TenantWriteMismatchException : InvalidOperationException
{
    public string EntityType { get; }
    public Guid ExpectedAccountId { get; }
    public Guid ActualAccountId { get; }

    public TenantWriteMismatchException(
        string entityType, string state, Guid expectedAccountId, Guid actualAccountId)
        : base($"Refusing to save {state} {entityType}: AccountId {actualAccountId} does not match the " +
               $"resolved tenant {expectedAccountId}.")
    {
        EntityType = entityType;
        ExpectedAccountId = expectedAccountId;
        ActualAccountId = actualAccountId;
    }
}
