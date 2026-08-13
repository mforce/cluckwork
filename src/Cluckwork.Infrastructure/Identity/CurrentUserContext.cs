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
    // Roles stays empty on purpose — but READ THE NEXT PARAGRAPH before treating
    // that as "unprivileged", because it is not.
    //
    // Empty roles means FlockScopeGuard's role bypass does not apply. It does
    // NOT mean the actor is restricted: the guard then looks for
    // UserRoleAssignment rows, a system actor has none, and zero rows is its
    // "unscoped, account-wide" case. So a system actor has MORE effective flock
    // reach than a restricted worker, not less.
    //
    // That is acceptable only because of who calls this: `bootstrap-admin` and
    // `recover-admin` touch no flock-scoped handler at all. A future system
    // actor that reaches RecordDailyEntry, SubmitDailyEntry, RecordFeedUsage or
    // RecordWaterUsage would silently bypass flock scoping, and needs its own
    // answer rather than this one.
    public void ResolveSystemActor(string label)
    {
        UserId = Guid.Empty;
        Email = label;
        Roles = [];
        IsResolved = true;
    }
}
