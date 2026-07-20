namespace Cluckwork.Application.Features.Users;

using Cluckwork.Domain.Accounts;

public interface IUserRoleAssignmentRepository
{
    Task<IReadOnlyList<UserRoleAssignment>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserRoleAssignment>> ListAllAsync(CancellationToken ct = default);
    Task<UserRoleAssignment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(UserRoleAssignment assignment, CancellationToken ct = default);
    void Remove(UserRoleAssignment assignment);
}
