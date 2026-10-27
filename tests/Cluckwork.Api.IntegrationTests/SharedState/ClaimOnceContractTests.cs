using System.Collections.Concurrent;
using System.Diagnostics;
using Cluckwork.Infrastructure.SharedState;

namespace Cluckwork.Api.IntegrationTests.SharedState;

// #543 — contract: single-use claim with TTL.
//
// Implementation-under-test: the in-process fallback via a single swappable
// factory. A later increment re-points this suite at Redis by changing
// <see cref="CreateStore"/> only — the assertions are the contract and stay
// byte-identical.
public sealed class ClaimOnceContractTests
{
    private static IClaimOnceStore CreateStore(TimeProvider timeProvider) =>
        new InProcessClaimOnceStore(timeProvider);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FakeTimeProvider();
            Store = CreateStore(Clock);
        }

        public FakeTimeProvider Clock { get; }
        public IClaimOnceStore Store { get; }
    }

    [Fact]
    public void FirstClaim_Succeeds()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SecondClaim_BeforeTtl_Fails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));

        Assert.False(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Claim_AfterTtlElapses_SucceedsAgain()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("a", TimeSpan.FromMinutes(5)));
        Assert.True(fixture.Store.TryClaim("b", TimeSpan.FromMinutes(5)));

        // Claiming "a" again is refused while its TTL is live, even though
        // "b" was claimed in between.
        Assert.False(fixture.Store.TryClaim("a", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Claim_AtExactExpiryInstant_SucceedsAgain()
    {
        // now == expires means the TTL has ELAPSED (the window is half-open):
        // the key is claimable again. A `<`->`<=` mutation on the liveness check
        // would wrongly keep the claim held at this instant.
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5)); // exactly to expiry

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    // #1372 — the race guard for single use. Eight threads claim each of 2,000 keys
    // together, and every key must have exactly one winner. It is probabilistic and
    // needs at least two cores to overlap the claims: a check-then-act store
    // double-admitted 15 to 158 keys per run on four cores, and 3 to 10 on two.
    //
    // The workers are dedicated threads, not pool threads, so their barrier waits
    // starve nothing else. A claim that throws is recorded and its worker stays in
    // the barrier, so an unlocked store still shows its double admissions.
    [Fact]
    public void ConcurrentClaims_EveryKeyHasExactlyOneWinner()
    {
        const int threads = 8;
        const int keys = 2000;
        var barrierWatchdog = TimeSpan.FromSeconds(30);
        var joinWatchdog = TimeSpan.FromSeconds(60);
        var store = new Fixture().Store;
        var winners = new int[keys];
        var faults = new ConcurrentQueue<Exception>();
        var barrierTimeouts = 0;
        // Neither is disposed while a worker may still run: a late worker would
        // touch a disposed object and take the test host down.
        var stop = new CancellationTokenSource();
        var barrier = new Barrier(threads);

        void Claim()
        {
            try
            {
                for (var k = 0; k < keys && !stop.IsCancellationRequested; k++)
                {
                    if (!barrier.SignalAndWait(barrierWatchdog, stop.Token))
                    {
                        Interlocked.Increment(ref barrierTimeouts);
                        stop.Cancel();
                        return;
                    }
                    try
                    {
                        if (store.TryClaim($"k{k}", TimeSpan.FromMinutes(5)))
                            Interlocked.Increment(ref winners[k]);
                    }
                    catch (Exception ex)
                    {
                        faults.Enqueue(ex);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                faults.Enqueue(ex);
                stop.Cancel();
            }
        }

        var started = new List<Thread>();
        var hung = 0;
        try
        {
            for (var i = 0; i < threads; i++)
            {
                var worker = new Thread(Claim) { IsBackground = true };
                worker.Start();
                started.Add(worker);
            }
        }
        finally
        {
            // One deadline for all joins, so a hang fails in one watchdog period.
            var joinStarted = Stopwatch.GetTimestamp();
            foreach (var worker in started)
            {
                var left = joinWatchdog - Stopwatch.GetElapsedTime(joinStarted);
                if (!worker.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                {
                    hung++;
                    stop.Cancel();
                }
            }
            if (hung == 0)
            {
                barrier.Dispose();
                stop.Dispose();
            }
        }

        var doubleWon = winners.Count(w => w > 1);
        var unclaimed = winners.Count(w => w == 0);
        var failure = hung > 0 || barrierTimeouts > 0 ? "WATCHDOG"
            : doubleWon > 0 || unclaimed > 0 ? "INVARIANT"
            : faults.IsEmpty ? null : "FAULT";
        Assert.True(failure is null,
            $"{failure}: hung={hung} barrierTimeouts={barrierTimeouts} doubleWon={doubleWon} unclaimed={unclaimed} " +
            $"faults={faults.Count} " +
            string.Join(" | ", faults.Take(3).Select(e => $"{e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n')[0].Trim()}")));
    }
}
