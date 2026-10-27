using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Modules.Access.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// A smoke test, not the race guard. Racers on their own registry contexts consume
// one jti while logouts for an unrelated user write StepUpLogoutEpoch on the same
// database, and exactly one consumer must win. Whether the racers overlap inside
// the claim is luck, so this test catches a store that always admits but not a
// racy one. The race guard is ClaimOnceContractTests.ConcurrentClaims_EveryKeyHasExactlyOneWinner;
// the call shape and the consume-before-read order are pinned by
// StepUpAuthTests.ValidateAsync_MakesTheAdmissionDecisionInOneAtomicRegistryCall and
// StepUpGrantRegistrySharedStoreTests.ALogoutLandingInTheGapBetweenConsumeAndRead_IsCaught.
//
// The racers wait on an async gate and hold no pool threads. A Barrier here once
// parked 16 pool threads and stalled every test running beside it for seconds (#1372).
// The class still runs alone so its burst of database traffic slows no other test.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StepUpGrantRegistryConcurrencySmokeCollection
{
    public const string Name = "step-up-grant-registry-concurrency-smoke";
}

[Collection(StepUpGrantRegistryConcurrencySmokeCollection.Name)]
public sealed class StepUpGrantRegistryConcurrencySmokeTests(CluckworkWebApplicationFactory factory)
    : IClassFixture<CluckworkWebApplicationFactory>
{
    private static readonly DateTimeOffset T = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RoundWatchdog = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task UnderConcurrentLogoutTraffic_AJtiIsConsumedExactlyOnce()
    {
        const int rounds = 30;
        const int racers = 8;

        var (accountId, userId) = await SeedUserAsync();
        var (otherAccountId, otherUserId) = await SeedUserAsync();

        for (var round = 0; round < rounds; round++)
        {
            var jti = Guid.NewGuid();
            var now = T.AddMinutes(round);
            var claimOnce = new InProcessClaimOnceStore(FixedFakeTimeProvider.At(now));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watchdog = new CancellationTokenSource(RoundWatchdog);
            var ct = watchdog.Token;
            var winners = 0;
            var dbs = new List<AppDbContext>();
            var tasks = new List<Task>();
            try
            {
                for (var i = 0; i < racers; i++)
                {
                    var consumeDb = NewDb(accountId, dbs);
                    tasks.Add(Task.Run(async () =>
                    {
                        await start.Task.WaitAsync(ct);
                        if (await new PersistentStepUpGrantRegistry(claimOnce, consumeDb)
                                .TryConsumeIfNotLoggedOutAsync(userId, jti, grantEpoch: 0, now.AddMinutes(5), now, ct))
                            Interlocked.Increment(ref winners);
                    }, ct));

                    var writerDb = NewDb(otherAccountId, dbs);
                    tasks.Add(Task.Run(async () =>
                    {
                        await start.Task.WaitAsync(ct);
                        await new PersistentStepUpGrantRegistry(
                            new InProcessClaimOnceStore(FixedFakeTimeProvider.At(now)), writerDb)
                            .RecordLogoutAsync(otherUserId, ct);
                    }, ct));
                }

                start.SetResult();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
                {
                    Assert.Fail($"WATCHDOG: round {round} did not finish within {RoundWatchdog.TotalSeconds} s");
                }
            }
            finally
            {
                start.TrySetCanceled();
                watchdog.Cancel();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception)
                {
                    // Only draining: the first failure is already propagating.
                }
                foreach (var db in dbs)
                    await db.DisposeAsync();
            }

            Assert.Equal(1, winners);
        }
    }

    private async Task<(Guid AccountId, Guid UserId)> SeedUserAsync()
    {
        var email = $"stepupsmoke-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(email);
        Assert.NotNull(user);
        return (accountId, user.Id);
    }

    // DbContext is not thread-safe, so every racer and writer gets its own.
    private AppDbContext NewDb(Guid accountId, List<AppDbContext> owned)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        owned.Add(db);
        return db;
    }
}
