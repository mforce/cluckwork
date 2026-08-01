namespace Cluckwork.Infrastructure.Identity;

using System.Collections.Concurrent;

// #308 — see IStepUpGrantRegistry for the design rationale. Registered as a
// SINGLETON (one instance per process): replay tracking and logout epochs
// must be visible across every request/scope, unlike the request-scoped
// TenantContext/CurrentUserContext.
public sealed class InMemoryStepUpGrantRegistry : IStepUpGrantRegistry
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _consumed = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _loggedOutAt = new();

    public bool TryConsume(Guid jti, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Prune(now);
        return _consumed.TryAdd(jti, expiresAt);
    }

    public void RecordLogout(Guid userId, DateTimeOffset at) =>
        _loggedOutAt.AddOrUpdate(userId, at, (_, existing) => at > existing ? at : existing);

    public bool IsRevokedByLogout(Guid userId, DateTimeOffset issuedAt) =>
        _loggedOutAt.TryGetValue(userId, out var loggedOutAt) && issuedAt <= loggedOutAt;

    // Opportunistic: drop expired consumption records so long process uptime
    // doesn't grow this table with every step-up grant ever issued. Not
    // applied to _loggedOutAt — that table has at most one entry per user who
    // has ever logged out, bounded by the user count, not request volume.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (jti, expiresAt) in _consumed)
            if (expiresAt < now) _consumed.TryRemove(jti, out _);
    }
}
