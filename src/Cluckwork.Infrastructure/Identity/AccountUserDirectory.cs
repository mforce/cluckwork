namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #532 — the ONE place production code resolves a user by an ambiguous
// identifier. Once an email can exist in several farms, every globally scoped
// Identity lookup is either wrong or a crash:
//
//   * UserStore.FindByEmailAsync uses SingleOrDefaultAsync, so it THROWS the
//     moment two farms share an address — and IdentityProvider.LoginAsync's
//     very first statement was one of these, so the first shared email broke
//     login for BOTH farms' users, not merely the duplicate-create path.
//   * UserStore.FindByNameAsync uses FirstOrDefaultAsync, so it silently
//     returns whichever farm Postgres happened to order first.
//   * GetUsersInRoleAsync loads every user in the role across every farm and
//     leaves the caller to post-filter, which is correct-by-convention today
//     and an O(all farms) cross-tenant read tomorrow.
//
// FindByIdAsync is deliberately NOT replaced: a Guid is unambiguous, and the
// two production callers (StepUpGrantService, PersistentStepUpGrantRegistry)
// already compare the account explicitly. Banning it would be a false guard.
public interface IAccountUserDirectory
{
    // Exactly one row or none: (AccountId, NormalizedEmail) is unique.
    Task<ApplicationUser?> FindByAccountEmailAsync(
        Guid accountId, string email, CancellationToken ct = default);

    // Members of one role within ONE account. Ordered by Id so a caller that
    // takes the first gets a deterministic answer across runs — the seeders'
    // determinism contract (#279) depends on it.
    Task<IReadOnlyList<ApplicationUser>> FindByAccountRoleAsync(
        Guid accountId, string roleName, CancellationToken ct = default);
}

// ApplicationUser carries NO tenant query filter (it is IdentityUser<Guid>, not
// Entity<TId>), so every method here compares AccountId EXPLICITLY. The filter
// is not doing it for us, and an unresolved TenantContext would match nothing.
internal sealed class AccountUserDirectory(AppDbContext db, ILookupNormalizer normalizer)
    : IAccountUserDirectory
{
    public Task<ApplicationUser?> FindByAccountEmailAsync(
        Guid accountId, string email, CancellationToken ct = default)
    {
        // Identity's own normalizer, not ToUpperInvariant: NormalizedEmail is
        // written by that normalizer, and a hand-rolled fold would silently
        // miss rows whose casing it maps differently.
        var normalized = normalizer.NormalizeEmail(email);
        return db.Users
            .Where(u => u.AccountId == accountId && u.NormalizedEmail == normalized)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationUser>> FindByAccountRoleAsync(
        Guid accountId, string roleName, CancellationToken ct = default)
    {
        // Matched on NormalizedName, because that is what UserManager's own
        // GetUsersInRoleAsync compares. Roles.Owner is the string "Admin", so a
        // hand-written "OWNER" here would be silently always-false.
        var normalizedRole = normalizer.NormalizeName(roleName);
        return await (
            from user in db.Users
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            where user.AccountId == accountId && role.NormalizedName == normalizedRole
            orderby user.Id
            select user).ToListAsync(ct);
    }
}

// Internal type, so the Api layer registers it through this extension rather
// than naming it — same shape as PersistentStepUpGrantRegistryRegistration.
public static class AccountUserDirectoryRegistration
{
    public static IServiceCollection AddAccountUserDirectory(this IServiceCollection services)
    {
        services.AddScoped<IAccountUserDirectory, AccountUserDirectory>();
        return services;
    }
}
