using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Access.Users;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

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
    IAccessLookup access,
    ICurrentUser user) : IFlockScopeGuard
{
    public async Task<Result> CheckAsync(Guid flockId, CancellationToken ct = default)
    {
        // #787 — authorization fails closed when a non-HTTP caller forgets to
        // declare its actor. This must happen before any assignment lookup.
        // HTTP requests resolve the actor in TenantResolutionMiddleware; both
        // seeders resolve a real user before invoking flock-scoped handlers.
        if (!user.IsResolved) return Result.Failure(AppError.Unauthorized());

        // #612 — only a plain Worker is ever flock-scoped. Owner, Manager,
        // Sales, ReadOnly and Denied all bypass assignment rows entirely.
        if (Roles.ResolveEffective(user.Roles) != EffectiveAccountRole.Worker)
            return Result.Success();

        var assigned = await access.GetAssignedFlocksAsync(user.UserId, ct);
        return assigned is null || assigned.Contains(flockId)
            ? Result.Success()
            : Result.Failure(Error.Domain(
                "FlockScope.NotAssigned",
                "You are not assigned to this flock — ask an owner or manager."));
    }
}
