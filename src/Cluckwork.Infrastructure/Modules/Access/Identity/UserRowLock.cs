using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 "Locks": the user-row lock every two-factor write takes first. It loads
// no entity, and AccountId is in the predicate, so it can lock only that
// farm's row. It holds until the caller's transaction ends.
internal static class UserRowLock
{
    internal static Task TakeAsync(AppDbContext db, Guid accountId, Guid userId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} AND "AccountId" = {accountId} FOR NO KEY UPDATE""", ct);
}
