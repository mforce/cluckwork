namespace Cluckwork.Application.Common;

// The acting actor of the current unit of work (#93) — resolved from the JWT
// beside TenantContext on an HTTP request, and by the caller itself on the
// non-HTTP paths: both seeders and the one-shot CLI verbs (#500).
//
// NOT audit metadata. Roles and UserId are an AUTHORIZATION input:
// FlockScopeGuard lets Owner and Manager past by role, treats a user with no
// UserRoleAssignment rows as account-wide, and narrows everyone else to their
// assigned flocks. RecordDailyEntry, SubmitDailyEntry, RecordFeedUsage and
// RecordWaterUsage all consult it. So what is resolved here decides what the
// caller MAY DO, not merely whose name lands on the audit row. This comment
// used to say "handlers use it only through IAuditWriter", and #500's plan
// reasoned from that for three revisions before anyone walked the consumers.
public interface ICurrentUser
{
    bool IsResolved { get; }
    Guid UserId { get; }
    string Email { get; }
    /// <summary>Role names from the token (#103). Empty for plain workers.</summary>
    IReadOnlyList<string> Roles { get; }
}
