namespace Cluckwork.Application.Features.Users;

using Cluckwork.Domain.Accounts;

public interface IUserRoleAssignmentRepository
{
    Task<IReadOnlyList<UserRoleAssignment>> ListByUserAsync(Guid userId, CancellationToken ct = default);

    // #512 T047 — the same rows, each with its flock's current name, from ONE
    // scoped left join. Unpaged on purpose: a worker's assignments are a bounded
    // operational list, and paging it would need an ordering the row set does not
    // have. LEFT JOIN because a farm-wide row (no flock) must survive with a null
    // name — an inner join drops it, which reads as missing data.
    //
    // The flock half of the join is resolved through the model's filtered Flocks
    // set, so a scoped Worker cannot learn the name of a flock they are not
    // assigned to; the assignment row itself still appears (it is the worker's
    // own assignment record), with a null name. That asymmetry is the intended
    // answer and is what the Worker-scope guard in FlockScopeTests pins.
    Task<IReadOnlyList<UserFlockAssignment>> ListByNameByUserAsync(
        Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserRoleAssignment>> ListAllAsync(CancellationToken ct = default);
    Task<UserRoleAssignment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(UserRoleAssignment assignment, CancellationToken ct = default);
    void Remove(UserRoleAssignment assignment);
}

// #512 T047 — an assignment with its flock's CURRENT name. FlockName is null for
// a farm-wide assignment (FlockId null) and also for an assignment naming a flock
// this caller may not see; the response contract pairs a null name with a null
// flock id, and the Worker case is guarded rather than relied on.
public sealed record UserFlockAssignment(Guid Id, Guid? FlockId, string? FlockName);
