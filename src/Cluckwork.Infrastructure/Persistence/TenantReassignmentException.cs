namespace Cluckwork.Infrastructure.Persistence;

// Distinct type so the failure is greppable in logs rather than read as a
// generic 500 — the same rationale FarmTimeZoneException carries
// (Infrastructure/Time/FarmClock.cs).
//
// Thrown when one DI scope is asked to serve two different accounts. See
// TenantContext for why that is never legitimate.
public sealed class TenantReassignmentException : InvalidOperationException
{
    public Guid ResolvedAccountId { get; }
    public Guid AttemptedAccountId { get; }

    public TenantReassignmentException(Guid resolvedAccountId, Guid attemptedAccountId)
        : base($"TenantContext is already resolved to account {resolvedAccountId} and cannot be " +
               $"re-resolved to {attemptedAccountId}. One scope serves exactly one account.")
    {
        ResolvedAccountId = resolvedAccountId;
        AttemptedAccountId = attemptedAccountId;
    }
}
