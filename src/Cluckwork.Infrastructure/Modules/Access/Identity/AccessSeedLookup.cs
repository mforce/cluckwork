using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed class AccessSeedLookup(
    IAccountUserDirectory directory,
    UserManager<ApplicationUser> users,
    AppDbContext db) : IAccessSeedLookup
{
    public async Task<AccessActor?> FindUserByEmailAsync(
        Guid accountId, string email, CancellationToken ct = default)
    {
        var user = await directory.FindByAccountEmailAsync(accountId, email, ct);
        return user is null ? null : await ActorAsync(user);
    }

    public async Task<AccessActor?> GetActorAsync(Guid accountId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.AccountId == accountId && u.Id == userId, ct);
        return user is null ? null : await ActorAsync(user);
    }

    public async Task<IReadOnlyList<AccessUserSummary>> ListUsersInRoleAsync(
        Guid accountId, string role, CancellationToken ct = default)
    {
        var members = await directory.FindByAccountRoleAsync(accountId, role, ct);
        return members.Select(u => new AccessUserSummary(u.Id, u.Email, u.DisabledAt)).ToArray();
    }

    public Task<bool> OwnerRoleExistsAsync(CancellationToken ct = default) =>
        db.Roles.AnyAsync(r => r.Name == Roles.Owner, ct);

    public Task<int> CountUsersAsync(Guid accountId, CancellationToken ct = default) =>
        db.Users.CountAsync(u => u.AccountId == accountId, ct);

    private async Task<AccessActor> ActorAsync(ApplicationUser user) =>
        new(user.Id, user.Email, user.DisabledAt, [.. await users.GetRolesAsync(user)]);
}
