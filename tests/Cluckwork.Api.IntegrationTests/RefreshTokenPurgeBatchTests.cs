using System.Collections.Concurrent;
using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #857 — #270 drains the purge in bounded batches so the first sweep over a
// months-old table is not one huge DELETE. RefreshTokenPurgeSweepTests checks
// that every aged row goes, which an unbounded DELETE also satisfies; this
// checks the bound itself, per statement.
public sealed class RefreshTokenPurgeBatchTests(RefreshTokenPurgeBatchTests.CountingFactory factory)
    : IClassFixture<RefreshTokenPurgeBatchTests.CountingFactory>
{
    public sealed class CountingFactory : CluckworkWebApplicationFactory
    {
        public PurgeDeleteCounter Counter { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Counter)));
        }
    }

    public sealed class PurgeDeleteCounter : DbCommandInterceptor
    {
        public ConcurrentQueue<int> RowsDeleted { get; } = new();

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM refresh_tokens", StringComparison.Ordinal))
                RowsDeleted.Enqueue(result);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task SeedAgedAsync(int count)
    {
        var expiresAt = DateTimeOffset.UtcNow - RefreshTokenPurgeSweep.PurgeGrace - TimeSpan.FromHours(1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.RefreshTokens.AddRange(Enumerable.Range(0, count).Select(_ => new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            CreatedAt = expiresAt.AddDays(-30),
            ExpiresAt = expiresAt,
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_DeletesAtMostOneBatchPerStatement()
    {
        var batch = RefreshTokenPurgeSweep.BatchSizeForTests;
        var sweep = factory.Services.GetRequiredService<RefreshTokenPurgeSweep>();

        // The host's own job worker also sweeps on its poll. If it takes the
        // backlog between the seed and this run, no statement here sees a full
        // batch, so seed and run again.
        for (var attempt = 1; ; attempt++)
        {
            await SeedAgedAsync(batch + 25);
            await sweep.RunAsync(CancellationToken.None);

            var deleted = factory.Counter.RowsDeleted.ToArray();
            Assert.All(deleted, rows => Assert.True(rows <= batch,
                $"one purge DELETE removed {rows} rows; the batch bound is {batch}"));
            if (deleted.Contains(batch))
                return;
            Assert.True(attempt < 3,
                $"no purge DELETE removed a full batch in {attempt} attempts: [{string.Join(", ", deleted)}]");
        }
    }
}
