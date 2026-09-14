namespace Cluckwork.Application.Common;

// The acting actor of the current unit of work (#93) — resolved from the JWT
// beside TenantContext on an HTTP request, and by the caller itself on the
// non-HTTP paths: both seeders and the one-shot CLI verbs (#500).
//
// NOT audit metadata. Roles and UserId are an AUTHORIZATION input:
// FlockScopeGuard refuses an unresolved actor (#787), lets non-Worker roles
// past by role, treats a Worker with no UserRoleAssignment rows as
// account-wide, and otherwise narrows the Worker to assigned flocks.
// RecordDailyEntry, SubmitDailyEntry, RecordFeedUsage and RecordWaterUsage all
// consult it. What is resolved here decides what the caller may do, not merely
// whose name lands on the audit row.
public interface ICurrentUser
{
    bool IsResolved { get; }
    Guid UserId { get; }
    string Email { get; }
    /// <summary>Role names from the token (#103). Empty for plain workers.</summary>
    IReadOnlyList<string> Roles { get; }
}
