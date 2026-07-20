namespace Cluckwork.Application.Common;

// The authenticated actor of the current request (#93) — resolved from the
// JWT beside TenantContext. Handlers use it only through IAuditWriter.
public interface ICurrentUser
{
    bool IsResolved { get; }
    Guid UserId { get; }
    string Email { get; }
    /// <summary>Role names from the token (#103). Empty for plain workers.</summary>
    IReadOnlyList<string> Roles { get; }
}
