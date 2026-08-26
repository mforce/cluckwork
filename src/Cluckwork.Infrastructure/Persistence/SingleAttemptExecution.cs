namespace Cluckwork.Infrastructure.Persistence;

using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

// #269 — the retry BOUNDARY.
//
// EnableRetryOnFailure (PostgresDbContextConfigurator) makes EF replay a
// failed unit of work. EF also refuses to let a user-initiated transaction be
// opened outside database.CreateExecutionStrategy().ExecuteAsync — because a
// transaction cannot be RESUMED after a connection loss, only replayed from
// the start, and EF will not do that behind your back.
//
// That guard is right, and it is the whole design constraint here: a
// transaction that spans work which is NOT replayable cannot be made
// resilient by retrying it. Four such regions exist in this codebase — note
// the fourth is NOT a transaction at all, which is the point: what disqualifies
// a unit from replay is that a replay is OBSERVABLE, and a transaction is only
// the most common way for that to be true.
//
//   * IdempotencyMiddleware's request-wide transaction, which by design (#307)
//     covers `next(context)` — the entire downstream HTTP pipeline. Replaying
//     it re-runs single-use, non-database claim state (the #308/#360 step-up
//     grant is consumed by CreateUserHandler, SetUserPasswordHandler, or
//     ChangeUserRoleHandler and can never be consumed twice) and re-runs a
//     domain state transition against state a prior attempt may already have
//     committed (the handler then answers 422 for a request that succeeded).
//   * An "owned" AmbientTransaction unit (AmbientTransaction.RunAsync), which
//     can leave the failed attempt's entities tracked as Added on the SAME
//     scoped AppDbContext — EF does not detach them, and a retry would flush
//     them alongside the fresh ones.
//   * FirstRunAdminService's whole lock -> check -> create region (#350 review
//     round 2). Wrapping only the create was not enough: the READS that decide
//     whether to create were ordinary EF units, so the strategy retried them,
//     and a retry RECONNECTS — dropping the SESSION-scoped pg_advisory_lock
//     and leaving the create to run unguarded. Nesting matters here: inside an
//     execution strategy EF suspends retries for every nested operation, which
//     is exactly why wrapping the OUTER region is what stops the inner reads
//     being replayed.
//   * AccountLockout's AccessFailedAsync save (#350 review round 4). No
//     transaction here — a plain SaveChanges, exactly the "self-contained
//     unit" the retry is FOR. What makes it unreplayable is the CALLER: a
//     replay of an already-committed increment comes back as an Identity
//     concurrency failure, and the caller's reload loop cannot tell that apart
//     from losing a race to a parallel writer, so it increments a SECOND time.
//     One wrong password would then cost two failed accesses and lock the
//     account at half the configured threshold. See AccountLockout.
//
// So those regions run through the execution strategy (satisfying EF's guard)
// but EXACTLY ONCE: the operation is never replayed, because the first
// failure is captured as a value rather than thrown out of ExecuteAsync, and
// rethrown — with its original stack — once the strategy has returned.
// Wrapping the exception instead would NOT work: EF's ExecutionStrategy walks
// the whole InnerException chain when it classifies, so a transient failure
// stays "retryable" no matter what it is wrapped in.
//
// What this gives up, deliberately: a transient failure INSIDE one of those
// regions surfaces as an error rather than being absorbed. For a write
// request that is the pre-#269 behaviour, and the codebase already has the
// right mechanism for it — the client retries with the same Idempotency-Key
// and #307's claim/lease/publish protocol makes that exactly-once. Everything
// EF runs as a self-contained unit (every read, and every SaveChanges outside
// one of these transactions) keeps its automatic retry.
public static class SingleAttemptExecution
{
    public static async Task<T> RunAsync<T>(DatabaseFacade database, Func<Task<T>> work)
    {
        var result = default(T)!;
        ExceptionDispatchInfo? failure = null;

        await database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            try
            {
                result = await work();
            }
            catch (Exception ex)
            {
                // Captured, not rethrown: the strategy must see this attempt
                // as terminal, whatever Npgsql would classify the failure as.
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        failure?.Throw();
        return result;
    }

    public static Task RunAsync(DatabaseFacade database, Func<Task> work) =>
        RunAsync(database, async () =>
        {
            await work();
            return true;
        });
}
