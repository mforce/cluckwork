using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1282 decision 3: an expired ceremony can never finish, so deleting it at
// its ExpiresAt loses nothing. Autofill inserts one per sign-in page load, so
// this bounds the table.
internal sealed class PasskeyCeremonyPurge(AppDbContext db) : IPasskeyCeremonyPurge
{
    public Task<int> DeleteExpiredBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) =>
        db.PasskeyCeremonies
            .Where(c => c.ExpiresAt < cutoff)
            .OrderBy(c => c.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(ct);
}
