using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Identity;

// #857 — depends on the request's scoped AppDbContext and nothing else, because
// the flock-scope middleware resolves it on every request. Stateless.
public sealed class AccessLookup(AppDbContext db) : IAccessLookup
{
    public Task<EffectiveAccountRole?> GetEffectiveRoleAsync(
        Guid accountId, Guid userId, CancellationToken ct = default) =>
        ReadEffectiveRoleAsync(db, accountId, userId, ct);

    public async Task<IReadOnlyList<FlockAssignmentDetails>> ListFlockAssignmentsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // The tenant query filter scopes this read. Under an unresolved tenant
        // it would return no rows, and no rows means account-wide access, so
        // refuse rather than fail open.
        if (!db.Tenant.IsResolved)
            throw new InvalidOperationException(
                "Flock assignments require a resolved tenant: an unresolved tenant reads no rows, " +
                "and no rows would grant account-wide access.");

        var assignments = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .ToListAsync(ct);
        return assignments.Select(a => new FlockAssignmentDetails(a.Id, a.FlockId)).ToList();
    }

    // #612 — a fresh, account-scoped read of the CURRENT effective role, for
    // callers that must re-verify live rather than trust a JWT claim minted
    // earlier in the request. A DISABLED user has NO effective role: the
    // account-membership check alone returned the role of a user disabled after
    // the request passed middleware, so a request parked on a lock resumed with
    // the authority it held when it queued. Existence is not authority — the
    // predicate is active membership. IdentityProvider.GetEffectiveRoleAsync runs
    // this same routine, so the two can never disagree.
    internal static async Task<EffectiveAccountRole?> ReadEffectiveRoleAsync(
        AppDbContext db, Guid accountId, Guid userId, CancellationToken ct)
    {
        var isActive = await db.Users.AsNoTracking().AnyAsync(
            u => u.Id == userId && u.AccountId == accountId && u.DisabledAt == null, ct);
        if (!isActive) return null;

        var roleNames = await (
            from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name!).ToListAsync(ct);
        return Roles.ResolveEffective(roleNames);
    }
}
