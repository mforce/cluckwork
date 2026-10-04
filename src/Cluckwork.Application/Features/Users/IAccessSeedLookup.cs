namespace Cluckwork.Application.Features.Users;

// Fixture actor reads; registered beside the seeders outside Production.
public interface IAccessSeedLookup
{
    Task<AccessActor?> FindUserByEmailAsync(Guid accountId, string email, CancellationToken ct = default);

    Task<AccessActor?> GetActorAsync(Guid accountId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessUserSummary>> ListUsersInRoleAsync(
        Guid accountId, string role, CancellationToken ct = default);
}

public sealed record AccessActor(Guid Id, string? Email, DateTimeOffset? DisabledAt, IReadOnlyList<string> Roles);

public sealed record AccessUserSummary(Guid Id, string? Email, DateTimeOffset? DisabledAt);
