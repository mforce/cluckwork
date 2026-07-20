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
        // Non-HTTP system callers (startup/demo seeders) have no resolved
        // user and are account-level by definition; every HTTP route behind
        // this guard also requires authentication, so a real request always
        // arrives resolved.
        if (!user.IsResolved) return Result.Success();

        if (user.Roles.Contains(Roles.Owner) || user.Roles.Contains(Roles.Manager))
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
