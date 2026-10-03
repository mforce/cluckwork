using Cluckwork.Application.Features.Users;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Identity;

public sealed class RefreshTokenPurge(AppDbContext db) : IRefreshTokenPurge
{
    // Strictly older than the cutoff. Exact equality is a measure-zero
    // case against a moving clock and carries no guarantee worth
    // pinning — what matters, and what the tests pin, is the retention
    // WINDOW: a row is safe until its own ExpiresAt plus the grace.
    public Task<int> DeleteExpiredBatchAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff)
            .OrderBy(t => t.ExpiresAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(ct);
}
