namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Identity;

// #308 / PR #336 review (3rd round) — the step-up registry's own contract, at
// the unit level. No Docker and no Postgres fixture (hence no
// IntegrationCollection, same as HealthCheckCliCommandTests): the registry is
// pure in-process state, and testing it directly is the only way to pin the
// exact-tick boundaries and the "left unconsumed" property that an HTTP round
// trip can only observe as "some request came back 403".
//
// The finding these back up: the admission decision used to be TWO registry
// calls in StepUpGrantService — IsRevokedByLogout then TryConsume — over two
// independently-atomic ConcurrentDictionaries. A logout completing between
// them was invisible to the validation in flight, so a grant minted before a
// logout could still create an Owner or reset an Owner's password after it.
// The decision is now one operation, TryConsumeIfNotLoggedOut, serialised
// against RecordLogout on a single lock. StepUpAuthTests'
// ValidateAsync_MakesTheAdmissionDecisionInOneAtomicRegistryCall pins the
// call SHAPE; this file pins the SEMANTICS of the operation it calls.
public sealed class StepUpGrantRegistryTests
{
    // A fixed, whole-second UTC anchor so every offset below is exact and the
    // sub-second boundaries are unambiguous. Not DateTimeOffset.UtcNow —
    // nothing here should depend on the wall clock.
    private static readonly DateTimeOffset T =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    // Comfortably inside every grant's lifetime, so nothing below is pruned
    // by accident — Prune is exercised deliberately by its own test.
    private static DateTimeOffset Expiry => T.AddMinutes(5);

    // ---------- Logout revocation refuses AND leaves the jti unconsumed ----------

    // The ordering half of the contract. A logout-revoked grant is refused, and
    // it must not burn its replay slot on the way out: it never got to act, so
    // consuming its jti would be recording a use that did not happen. Proven
    // POSITIVELY — by showing the very same jti is still consumable afterwards
    // — rather than by peering at the table, so the assertion holds against any
    // implementation of the interface.
    [Fact]
    public void ARevokedGrantIsRefused_AndItsJtiIsLeftUnconsumed()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();
        var jti = Guid.NewGuid();

        registry.RecordLogout(userId, T);

        // Issued before the logout → refused.
        Assert.False(registry.TryConsumeIfNotLoggedOut(
            userId, jti, issuedAt: T.AddSeconds(-1), Expiry, now: T));

        // …and the jti was never marked used: the SAME jti, now presented on a
        // grant issued after the logout, is still admitted. If the refusal had
        // consumed it this would come back false.
        Assert.True(registry.TryConsumeIfNotLoggedOut(
            userId, jti, issuedAt: T.AddSeconds(1), Expiry, now: T));
    }

    // The over-revocation guard, at the unit level: recording a logout bounds
    // the PAST only. A grant minted afterwards is a fresh, deliberate
    // re-authentication and must work, or one logout would permanently lock the
    // user out of privileged administration.
    [Fact]
    public void AGrantIssuedAfterTheLogout_ConsumesNormally()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        registry.RecordLogout(userId, T);

        Assert.True(registry.TryConsumeIfNotLoggedOut(
            userId, Guid.NewGuid(), issuedAt: T.AddMinutes(1), Expiry, now: T.AddMinutes(1)));
    }

    // A logout is per-user. One user's logout must not revoke another's grant.
    [Fact]
    public void ALogoutRevokesOnlyThatUsersGrants()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var loggedOut = Guid.NewGuid();
        var other = Guid.NewGuid();

        registry.RecordLogout(loggedOut, T);

        Assert.False(registry.TryConsumeIfNotLoggedOut(
            loggedOut, Guid.NewGuid(), issuedAt: T.AddSeconds(-1), Expiry, now: T));
        Assert.True(registry.TryConsumeIfNotLoggedOut(
            other, Guid.NewGuid(), issuedAt: T.AddSeconds(-1), Expiry, now: T));
    }

    // ---------- Replay ----------

    [Fact]
    public void ASecondConsumeOfTheSameJti_IsRefused()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();
        var jti = Guid.NewGuid();

        Assert.True(registry.TryConsumeIfNotLoggedOut(userId, jti, issuedAt: T, Expiry, now: T));
        Assert.False(registry.TryConsumeIfNotLoggedOut(userId, jti, issuedAt: T, Expiry, now: T));
    }

    // Replay tracking is keyed by jti alone — a second, DIFFERENT grant for the
    // same user is a separate token and must be admitted. Otherwise using one
    // grant would lock the user out of taking another.
    [Fact]
    public void ADifferentJtiForTheSameUser_IsStillAdmitted()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        Assert.True(registry.TryConsumeIfNotLoggedOut(userId, Guid.NewGuid(), issuedAt: T, Expiry, now: T));
        Assert.True(registry.TryConsumeIfNotLoggedOut(userId, Guid.NewGuid(), issuedAt: T, Expiry, now: T));
    }

    // ---------- The at-or-before boundary, BOTH sides ----------
    //
    // The documented comparison is `issuedAt <= loggedOutAt` → revoked. The
    // three cases below straddle the exact tick where `<=` and `<` disagree, so
    // flipping the operator either way fails one of them. Probing only one side
    // would leave the wrong operator green — the exact-instant case is the one
    // that catches `<`, and the one-tick-after case is the one that catches a
    // sloppy `>=`-style over-revocation.

    [Fact]
    public void AtTheExactLogoutInstant_TheGrantIsRevoked()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        registry.RecordLogout(userId, T);

        // issuedAt == loggedOutAt. "At or before" is the contract: refused.
        Assert.False(registry.TryConsumeIfNotLoggedOut(
            userId, Guid.NewGuid(), issuedAt: T, Expiry, now: T));
        Assert.True(registry.IsRevokedByLogout(userId, T));
    }

    [Fact]
    public void OneTickBeforeTheLogout_TheGrantIsRevoked()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        registry.RecordLogout(userId, T);

        Assert.False(registry.TryConsumeIfNotLoggedOut(
            userId, Guid.NewGuid(), issuedAt: T - OneTick, Expiry, now: T));
        Assert.True(registry.IsRevokedByLogout(userId, T - OneTick));
    }

    [Fact]
    public void OneTickAfterTheLogout_TheGrantIsAdmitted()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        registry.RecordLogout(userId, T);

        Assert.True(registry.TryConsumeIfNotLoggedOut(
            userId, Guid.NewGuid(), issuedAt: T + OneTick, Expiry, now: T));
        Assert.False(registry.IsRevokedByLogout(userId, T + OneTick));
    }

    // ---------- RecordLogout keeps the LATEST instant ----------
    //
    // One logout can reach the registry twice for the same user (the cookie
    // owner and the authenticated bearer are recorded independently —
    // IdentityProvider), and logouts arrive out of order under concurrency. The
    // epoch must only ever move forward, or a late-delivered EARLIER logout
    // would silently un-revoke grants a later one had already killed.
    [Fact]
    public void RecordLogout_KeepsTheLatestInstant_AndNeverMovesBackwards()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();

        registry.RecordLogout(userId, T.AddMinutes(10));
        registry.RecordLogout(userId, T); // earlier, arriving second

        // Both probes sit STRICTLY between the two instants, so this test turns
        // on monotonicity alone: had the epoch moved back to T, a grant issued
        // at T+5 would be after it and admitted. Deliberately not probing the
        // at-or-before boundary here — that belongs to the three boundary tests
        // above, and mixing the two would make a failure ambiguous.
        Assert.True(registry.IsRevokedByLogout(userId, T.AddMinutes(5)));
        Assert.False(registry.TryConsumeIfNotLoggedOut(
            userId, Guid.NewGuid(), issuedAt: T.AddMinutes(5), Expiry, now: T.AddMinutes(10)));
    }

    // ---------- Opportunistic pruning ----------
    //
    // The consumption table is bounded by dropping records whose grant has
    // expired — without it, a long-lived process accumulates one entry per
    // step-up grant ever issued. Observable exactly one way: once a record is
    // pruned, its jti is admissible again. That is harmless (the grant itself
    // is long expired and StepUpGrantService rejects it on expiry before ever
    // reaching the registry) and it is the behaviour the fix must preserve.
    [Fact]
    public void ExpiredConsumptionRecordsArePruned()
    {
        var registry = new InMemoryStepUpGrantRegistry();
        var userId = Guid.NewGuid();
        var jti = Guid.NewGuid();

        Assert.True(registry.TryConsumeIfNotLoggedOut(
            userId, jti, issuedAt: T, expiresAt: T.AddMinutes(1), now: T));

        // Still inside the record's lifetime — a replay is refused.
        Assert.False(registry.TryConsumeIfNotLoggedOut(
            userId, jti, issuedAt: T, expiresAt: T.AddMinutes(1), now: T.AddSeconds(30)));

        // Past it — the record is pruned, so the slot is reusable.
        Assert.True(registry.TryConsumeIfNotLoggedOut(
            userId, jti, issuedAt: T, expiresAt: T.AddMinutes(5), now: T.AddMinutes(2)));
    }

    // ---------- SUPPLEMENTARY: concurrency ----------
    //
    // Deliberately labelled supplementary. A timing test can pass by luck, so
    // it guards NOTHING on its own — the deterministic guards are the
    // spy-registry test in StepUpAuthTests (the call shape) plus the semantic
    // cases above. What this adds is the one property only concurrency can
    // show: the invariant holds while RecordLogout is hammering the same
    // instance, which is the traffic pattern the finding was about.
    //
    // The invariant asserted is single-use under contention: N threads racing
    // to consume ONE jti must produce EXACTLY one winner. Concurrent
    // RecordLogout traffic on unrelated users runs alongside, so both tables
    // are being mutated from multiple threads throughout.
    [Fact]
    public async Task Supplementary_UnderConcurrentLogoutTraffic_AJtiIsConsumedExactlyOnce()
    {
        const int rounds = 300;
        const int racers = 8;
        var registry = new InMemoryStepUpGrantRegistry();

        for (var round = 0; round < rounds; round++)
        {
            var userId = Guid.NewGuid();
            var jti = Guid.NewGuid();
            var issuedAt = T.AddMinutes(round);

            using var start = new Barrier(racers * 2);
            var winners = 0;

            var tasks = new List<Task>();
            for (var i = 0; i < racers; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    start.SignalAndWait();
                    if (registry.TryConsumeIfNotLoggedOut(
                            userId, jti, issuedAt, issuedAt.AddMinutes(5), issuedAt))
                        Interlocked.Increment(ref winners);
                }));

                // Logouts for OTHER users: they must not affect this round's
                // outcome, but they keep the logout table under concurrent
                // mutation while the consumes run.
                var otherUser = Guid.NewGuid();
                tasks.Add(Task.Run(() =>
                {
                    start.SignalAndWait();
                    registry.RecordLogout(otherUser, issuedAt);
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Equal(1, winners);
        }
    }
}
