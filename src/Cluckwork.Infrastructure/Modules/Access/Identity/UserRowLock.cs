using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Locks": the user-row lock every two-factor and passkey write takes
// first. It loads no entity, and AccountId is in the predicate, so it can lock
// only that farm's row. It holds until the caller's transaction ends.
internal static class UserRowLock
{
    internal static Task TakeAsync(AppDbContext db, Guid accountId, Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} AND "AccountId" = {accountId} FOR NO KEY UPDATE""", ct);

    // The lock, then the user as it stands under it, so every check and update
    // is exact under concurrency. A tracked query returns the identity-map
    // instance with the values it read before the lock, hence the reload; the
    // reload would discard a caller's unsaved change, hence the throw.
    internal static async Task<ApplicationUser?> LoadLockedAsync(
        AppDbContext db, Guid accountId, Guid userId, CancellationToken ct)
    {
        if (db.ChangeTracker.Entries<ApplicationUser>().Any(e => e.Entity.Id == userId && e.State == EntityState.Modified))
            throw new InvalidOperationException("Save the user's tracked changes before a credential write; the lock reloads the row.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Credential writes run inside the caller's transaction, which holds the lock.");

        await TakeAsync(db, accountId, userId, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is not null)
            await db.Entry(user).ReloadAsync(ct);
        return user;
    }
}
