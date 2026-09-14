namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;

// The acting actor for the current unit of work. Scoped.
//
// Usually resolved by TenantResolutionMiddleware from the JWT's sub + email
// claims (TenantContext pattern) — but NOT only: since #500 the CLI verbs and
// both seeders resolve their own actor, because IAuditWriter fails closed on an
// unresolved one.
//
// This is NOT audit-only metadata. FlockScopeGuard reads Roles and UserId as an
// authorization input, so what is resolved here decides what the caller may do,
// not merely whose name lands on the audit row (#500 — the plan for that issue
// asserted the opposite through three revisions before anyone walked the
// consumers).
public sealed class CurrentUserContext : ICurrentUser
{
    public bool IsResolved { get; private set; }
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public IReadOnlyList<string> Roles { get; private set; } = [];

    // Stores its arguments verbatim — no database read, no role re-fetch. The
    // seeders depend on that: the Roles list they pass is exactly the one
    // FlockScopeGuard later reads.
    public void Resolve(Guid userId, string email, IReadOnlyList<string>? roles = null)
    {
        UserId = userId;
        Email = email;
        Roles = roles ?? [];
        IsResolved = true;
    }

    // #500 — a caller with no human actor at all (the one-shot CLI verbs)
    // declaring WHICH non-person it is. ActorUserId stays Guid.Empty, which is
    // what those audit rows have always carried; the difference is that the
    // label is now chosen deliberately instead of defaulted to "(unresolved)"
    // by a fallback nobody could see.
    //
    // Roles stays empty on purpose. If a system actor later reaches a
    // flock-scoped handler, FlockScopeGuard will treat its zero assignment rows
    // as account-wide. No current system-actor caller reaches such a handler;
    // adding one requires an explicit review of that access.
    public void ResolveSystemActor(string label)
    {
        UserId = Guid.Empty;
        Email = label;
        Roles = [];
        IsResolved = true;
    }
}
