namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class UserRoleAssignmentRepository(AppDbContext db) : IUserRoleAssignmentRepository
{
    public async Task<IReadOnlyList<UserRoleAssignment>> ListByUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);


    public async Task<IReadOnlyList<UserFlockAssignment>> ListByNameByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        // ONE statement. The shape here is load-bearing and is asserted, not
        // trusted: the obvious-looking `Select(a => db.Flocks.Where(...).FirstOrDefault())`
        // projects a CORRELATED SCALAR SUBQUERY — a per-row lookup folded into the
        // projection, which is the N+1 in the exact place the contract forbids it.
        // It returns identical data, so no response assertion can tell the two
        // apart; the statement can. An explicit join (join…into…DefaultIfEmpty)
        // renders LEFT JOIN and costs one pass over the flock primary keys for the
        // whole list.
        //
        // The join target is the FILTERED DbSet, so the model's `AccountId AND
        // flock-scope` filter decides which names this caller may see (#613);
        // IgnoreQueryFilters() here is what the Worker-scope guard reddens.
        // A GroupJoin with DefaultIfEmpty is what renders LEFT JOIN; an inner Join
        // would drop the farm-wide row (FlockId null) entirely, which reads as
        // missing data rather than as account-wide access.
        var rows = await (
            from a in db.UserRoleAssignments.AsNoTracking().Where(x => x.UserId == userId)
            join f in db.Flocks.AsNoTracking() on a.FlockId equals f.Id into matched
            from f in matched.DefaultIfEmpty()
            orderby a.Id
            select new UserFlockAssignment(a.Id, a.FlockId, f == null ? null : f.Name))
            .TagWith(ReferenceMarkers.AssignmentProjection)
            .ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<UserRoleAssignment>> ListAllAsync(CancellationToken ct = default) =>
        await db.UserRoleAssignments.AsNoTracking().ToListAsync(ct);

    public Task<UserRoleAssignment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.UserRoleAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task AddAsync(UserRoleAssignment assignment, CancellationToken ct = default) =>
        await db.UserRoleAssignments.AddAsync(assignment, ct);

    public void Remove(UserRoleAssignment assignment) =>
        db.UserRoleAssignments.Remove(assignment);
}

// #103 — spec §5.3 flock scoping. Elevated roles skip the check entirely; a
// worker is narrowed only once assignment rows exist.
public sealed class FlockScopeGuard(
    AppDbContext db,
    ICurrentUser user) : IFlockScopeGuard
{
    public async Task<Result> CheckAsync(Guid flockId, CancellationToken ct = default)
    {
        // #500 — this branch FAILS OPEN, and its old justification was false in a
        // security-relevant way. Read the whole comment before touching it.
        //
        // It used to read: "non-HTTP system callers (startup/demo seeders) have
        // no resolved user and are account-level by definition." That named the
        // seeders as the reason for a bypass in an AUTHORIZATION path, and the
        // seeders were in fact the only callers taking it. They now resolve a
        // real actor, so the bypass they justified is gone with them.
        //
        // It is tempting to call the branch unreachable. That is TRUE FOR ONLY
        // HALF THE CALLERS, and the #500 plan asserted the whole of it before
        // anyone checked — the correction is worth stating exactly:
        //
        //   * RecordDailyEntry and SubmitDailyEntry DO write an audit event, and
        //     IAuditWriter fails closed on an unresolved actor, so an unresolved
        //     caller throws there before it ever reaches this line.
        //   * RecordFeedUsage and RecordWaterUsage audit NOTHING. For those two,
        //     an unresolved caller arrives here and is granted account-wide
        //     access, silently. Nothing downstream catches it.
        //
        // No such caller exists today: both seeders declare an actor before every
        // feed/water-usage call, and every HTTP route behind this guard requires
        // authentication. So this is a live gap for a FUTURE non-HTTP caller, not
        // a present defect — and the fix for such a caller is to make it declare
        // an actor, not to lean on this branch.
        //
        // Closing the gap here means flipping an authorization default from open
        // to closed, which is a behaviour change beyond #500's scope and deserves
        // its own issue rather than a drive-by. Documented rather than silently
        // narrowed.
        if (!user.IsResolved) return Result.Success();

        // #612 — only a plain Worker is ever flock-scoped. Owner, Manager,
        // Sales, ReadOnly and Denied all bypass assignment rows entirely.
        if (Roles.ResolveEffective(user.Roles) != EffectiveAccountRole.Worker)
            return Result.Success();

        var assignments = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == user.UserId)
            .ToListAsync(ct);

        // No rows = unscoped worker (grandfathered #73 behavior).
        if (assignments.Count == 0) return Result.Success();

        // A farm/house-wide row (no flock) grants everything in the
        // single-farm MVP; otherwise the flock must be assigned.
        return assignments.Any(a => a.FlockId == null || a.FlockId == flockId)
            ? Result.Success()
            : Result.Failure(Error.Domain(
                "FlockScope.NotAssigned",
                "You are not assigned to this flock — ask an owner or manager."));
    }
}
