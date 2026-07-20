namespace Cluckwork.Application.Features.Users.AssignFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;

// #103 (spec §5.2/§5.3) — narrow a worker to assigned flocks. The FIRST
// assignment flips the user from account-wide (grandfathered) to scoped.
public sealed class AssignFlockHandler(
    IUserRoleAssignmentRepository assignments,
    IFlockRepository flocks,
    IIdentityProvider identity,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result<Guid>> HandleAsync(
        Guid userId, Guid flockId, Guid accountId, CancellationToken ct)
    {
        // The target must be this account's user (no cross-tenant probing).
        var users = await identity.ListUsersAsync(accountId, ct);
        var target = users.FirstOrDefault(u => u.Id == userId);
        if (target is null)
            return Result.Failure<Guid>(Error.NotFound("User", userId));

        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound("Flock", flockId));

        if ((await assignments.ListByUserAsync(userId, ct)).Any(a => a.FlockId == flockId))
            return Result.Failure<Guid>(Error.Conflict(
                "Users.AlreadyAssigned", "This flock is already assigned to the user."));

        var assignment = UserRoleAssignment.Create(
            Guid.NewGuid(), accountId, userId, farmId: null, houseId: null, flockId);
        await assignments.AddAsync(assignment, ct);

        await audit.WriteAsync("User.FlockAssign", "User", userId,
            details: new { target.Email, Flock = flock.Name }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(assignment.Id);
    }
}

public sealed class UnassignFlockHandler(
    IUserRoleAssignmentRepository assignments,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(Guid assignmentId, CancellationToken ct)
    {
        var assignment = await assignments.GetByIdAsync(assignmentId, ct);
        if (assignment is null)
            return Result.Failure(Error.NotFound("UserRoleAssignment", assignmentId));

        assignments.Remove(assignment);

        await audit.WriteAsync("User.FlockUnassign", "User", assignment.UserId,
            details: new { assignment.FlockId }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
