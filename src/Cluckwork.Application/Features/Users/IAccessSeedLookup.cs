namespace Cluckwork.Application.Features.Users;

// Fixture actor reads; registered beside the seeders outside Production.
[ModuleContract("Access")]
public interface IAccessSeedLookup
{
    Task<AccessActor?> FindUserByEmailAsync(Guid accountId, string email, CancellationToken ct = default);

    Task<AccessActor?> GetActorAsync(Guid accountId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessUserSummary>> ListUsersInRoleAsync(
        Guid accountId, string role, CancellationToken ct = default);

    // Roles are global; the Owner role ships with the migrations (#283).
    Task<bool> OwnerRoleExistsAsync(CancellationToken ct = default);

    // Every user in the farm, including role-less Workers.
    Task<int> CountUsersAsync(Guid accountId, CancellationToken ct = default);
}

[ModuleContract("Access")]
public sealed record AccessActor(Guid Id, string? Email, DateTimeOffset? DisabledAt, IReadOnlyList<string> Roles);

[ModuleContract("Access")]
public sealed record AccessUserSummary(Guid Id, string? Email, DateTimeOffset? DisabledAt);
