namespace Cluckwork.Infrastructure.Persistence;

// Per-request flock-scope resolution (#388). Parallel to TenantContext (#546).
// Tri-state: Unrestricted (unresolved user, Owner/Manager, 0 assignment rows,
// or any farm-wide row) or RestrictedTo(assigned flock ids).
// Single-assignment: a differing re-resolve throws FlockScopeReassignmentException.
//
// Resolved by FlockScopeResolutionMiddleware from UserRoleAssignment rows (a DB
// read), NOT from a JWT claim. This is a different resolution contract than
// TenantContext (which resolves from a claim, no I/O).
//
// The query filter reads this (a constructor field of AppDbContext), not a
// service resolved at query time. The middleware populates it once per request;
// the filter reads the populated value on every query (no per-query DB read).
public sealed class FlockScope
{
    // Unresolved contexts are Unrestricted. HTTP middleware resolves every
    // request explicitly; design-time factories, seeders, one-shot verbs and
    // hand-built test contexts do not run that middleware and must retain the
    // existing account-wide read behavior (INV-3/INV-4, FlockScopeGuard line 70).
    public bool IsUnrestricted { get; private set; } = true;
    public IReadOnlyCollection<Guid> AssignedFlockIds { get; private set; } = [];
    public bool IsResolved { get; private set; }

    public void Resolve(bool unrestricted, IReadOnlyCollection<Guid> flockIds)
    {
        if (IsResolved)
        {
            // Same scope: a deliberate no-op, NOT an error (mirrors TenantContext).
            // (IReadOnlyCollection<Guid> has no SetEquals — compare as sets manually.)
            if (IsUnrestricted == unrestricted &&
                flockIds.Count == AssignedFlockIds.Count &&
                !flockIds.Except(AssignedFlockIds).Any())
                return;
            throw new FlockScopeReassignmentException(IsUnrestricted, AssignedFlockIds, unrestricted, flockIds);
        }

        IsUnrestricted = unrestricted;
        AssignedFlockIds = flockIds.ToList().AsReadOnly(); // defensive copy
        IsResolved = true;
    }
}

public sealed class FlockScopeReassignmentException(
    bool oldUnrestricted, IReadOnlyCollection<Guid> oldIds,
    bool newUnrestricted, IReadOnlyCollection<Guid> newIds)
    : Exception($"FlockScope reassignment: ({oldUnrestricted}, {oldIds.Count} ids) -> ({newUnrestricted}, {newIds.Count} ids)");
