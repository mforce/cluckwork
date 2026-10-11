using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cluckwork.Api.IntegrationTests;

// #271 — the worker's leadership gate. The poll (and therefore the sweeps) runs only
// while this instance is the leader; a follower does no work but stays healthy; a
// FAULTED acquisition (could not reach the lock) does no work AND does not stamp the
// heartbeat, so a sustained fault degrades /health. No database: the only thing
// ProcessPendingJobsAsync does first is ask the scope factory for a scope, so a
// counting scope factory observes whether the poll ran without any real work.
public sealed class DurableJobWorkerLeaderGateTests
{
    // Counts scope creations (proof the poll ran) and throws — the worker's guarded
    // iteration swallows the throw, so a "leader" cycle records the attempt without
    // needing a real database.
    private sealed class CountingScopeFactory : IServiceScopeFactory
    {
        private int _creates;
        public int Creates => Volatile.Read(ref _creates);
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _creates);
            throw new InvalidOperationException("no database in this unit test");
        }
    }

    private static DurableJobWorker Worker(
        IServiceScopeFactory scopeFactory, DurableJobWorkerHeartbeat heartbeat, ILeaderLease lease) =>
        new(scopeFactory,
            NullLogger<DurableJobWorker>.Instance,
            lease,
            heartbeat: heartbeat,
            pollInterval: TimeSpan.FromMilliseconds(5),
            initialBackoff: TimeSpan.FromMilliseconds(5));

    private static Task RunOnceAsync(
        IServiceScopeFactory scopeFactory, DurableJobWorkerHeartbeat heartbeat, SignallingLease lease) =>
        lease.RunWorkerThroughOneIterationAsync(Worker(scopeFactory, heartbeat, lease));

    [Fact]
    public async Task Follower_NeverPolls_ButStampsHeartbeat()
    {
        var scopeFactory = new CountingScopeFactory();
        var heartbeat = new DurableJobWorkerHeartbeat(TimeProvider.System);
        await RunOnceAsync(scopeFactory, heartbeat, new SignallingLease(LeaseStatus.Follower));

        Assert.Equal(0, scopeFactory.Creates);
        Assert.NotNull(heartbeat.LastSuccessfulPoll);
    }

    [Fact]
    public async Task Leader_Polls()
    {
        var scopeFactory = new CountingScopeFactory();
        var heartbeat = new DurableJobWorkerHeartbeat(TimeProvider.System);
        await RunOnceAsync(scopeFactory, heartbeat, new SignallingLease(LeaseStatus.Leader));

        Assert.True(scopeFactory.Creates > 0);
    }

    // The regression guard for the P1: a fault must NOT read as a healthy follower.
    [Fact]
    public async Task FaultedLease_NeverPolls_AndDoesNotStampHeartbeat()
    {
        var scopeFactory = new CountingScopeFactory();
        var heartbeat = new DurableJobWorkerHeartbeat(TimeProvider.System);
        await RunOnceAsync(scopeFactory, heartbeat, new SignallingLease(LeaseStatus.Faulted));

        Assert.Equal(0, scopeFactory.Creates);
        Assert.Null(heartbeat.LastSuccessfulPoll);
    }

    // Defence in depth: a lease that breaks the "never throw" contract is treated as
    // a fault — the host survives (no StopHost) and health is not falsely stamped.
    [Fact]
    public async Task ThrowingLease_IsTreatedAsFault_HostSurvives()
    {
        var scopeFactory = new CountingScopeFactory();
        var heartbeat = new DurableJobWorkerHeartbeat(TimeProvider.System);
        var lease = new SignallingLease(() => throw new InvalidOperationException("lease boom"));
        var worker = Worker(scopeFactory, heartbeat, lease);

        await lease.RunWorkerThroughOneIterationAsync(worker);

        Assert.False(worker.ExecuteTask!.IsFaulted);
        Assert.Equal(0, scopeFactory.Creates);
        Assert.Null(heartbeat.LastSuccessfulPoll);
    }
}
