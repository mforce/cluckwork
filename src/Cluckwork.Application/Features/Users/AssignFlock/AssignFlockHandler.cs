namespace Cluckwork.Application.Features.Users.AssignFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;

// #103 (spec §5.2/§5.3) — narrow a worker to assigned flocks. The FIRST
// assignment flips the user from account-wide (grandfathered) to scoped.
// #606 — every interactive assignment requires a fresh step-up grant, the
// same unconditional gating as the other durable user-access actions
// (#308/#360). Proof is validated FIRST, before any target/flock/duplicate
// lookup, so a missing/invalid grant discloses nothing about which targets
// exist.
public sealed class AssignFlockHandler(
    IUserRoleAssignmentRepository assignments,
    IFlockRepository flocks,
    IIdentityProvider identity,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IStepUpGrantService stepUp)
{
    public async Task<Result<Guid>> HandleAsync(
        AssignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return Result.Failure<Guid>(proof.Error);

        var userId = command.UserId;
        var flockId = command.FlockId;

        // The target must be this account's user (no cross-tenant probing).
        var users = await identity.ListUsersAsync(accountId, ct);
        var target = users.FirstOrDefault(u => u.Id == userId);
        if (target is null)
            return Result.Failure<Guid>(Error.NotFound("User", userId));

        // #612 — a fresh live read, not the JWT-issued caller's own claims and
        // not ListUsersAsync's raw-string Role: only a plain Worker may be
        // assigned a flock. Retained rows on an elevated user are inert (the
        // scope resolver bypasses them); this refuses NEW writes onto one.
        var targetRole = await identity.GetEffectiveRoleAsync(accountId, userId, ct);
        if (targetRole != EffectiveAccountRole.Worker)
            return Result.Failure<Guid>(Error.Validation(
                "Users.FlockAssignmentsWorkerOnly",
                "Flock assignments apply only to a plain Worker."));

        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound("Flock", flockId));

        if ((await assignments.ListByUserAsync(userId, ct)).Any(a => a.FlockId == flockId))
            return Result.Failure<Guid>(Error.Conflict(
                "Users.AlreadyAssigned", "This flock is already assigned to the user."));

        var assignment = UserRoleAssignment.Create(
            Guid.NewGuid(), accountId, userId, farmId: null, houseId: null, flockId);
        await assignments.AddAsync(assignment, ct);

        await audit.WriteAsync(AuditActions.UserFlockAssign, "User", userId,
            details: new { target.Email, Flock = flock.Name }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(assignment.Id);
    }
}

// #606 — unconditional step-up, same as AssignFlockHandler above. The
// endpoint is the ONLY caller: there is no trusted non-HTTP unassignment path.
public sealed class UnassignFlockHandler(
    IUserRoleAssignmentRepository assignments,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        UnassignFlockCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;

        var userId = command.UserId;
        var assignmentId = command.AssignmentId;

        var assignment = await assignments.GetByIdAsync(assignmentId, ct);
        // The assignment must belong to the ROUTE's user — a mismatched pair
        // must not delete another worker's assignment (and thereby widen them
        // back to account-wide access) — codex review of #104.
        if (assignment is null || assignment.UserId != userId)
            return Result.Failure(Error.NotFound("UserRoleAssignment", assignmentId));

        assignments.Remove(assignment);

        await audit.WriteAsync(AuditActions.UserFlockUnassign, "User", assignment.UserId,
            details: new { assignment.FlockId }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
