namespace Cluckwork.Api.RateLimiting;

using System.Threading.RateLimiting;

// #311 — caps concurrently in-flight report queries PER ACCOUNT, using the
// same System.Threading.RateLimiting primitives as the IP-keyed policies in
// CluckworkRateLimitingServiceCollectionExtensions (a ConcurrencyLimiter
// partitioned by key — nothing hand-rolled). It is a separate, directly
// injectable singleton rather than a named policy on the shared
// AddRateLimiter()/UseRateLimiter() pipeline because the partition key here
// (TenantContext.AccountId) is only resolved by TenantResolutionMiddleware,
// which runs AFTER the global UseRateLimiter() middleware in Program.cs — the
// same ordering the #309 body-limit placement deliberately preserves for the
// IP-keyed auth policies. ReportConcurrencyLimitFilter applies this as an
// endpoint filter instead, which runs once auth/tenant resolution is done.
//
// NO QUEUEING, deliberately: over-cap work is refused immediately (429 +
// Retry-After) rather than parked, which is what the #311 GLOSSARY entry and the
// in-app Help copy promise users ("try again shortly", not "waiting in line").
// A waiting acquire would also let one account's burst pin request threads and
// DB connections for the duration of the queue, which is the cost this cap
// exists to bound. So Acquire uses the non-waiting AttemptAcquire and the
// partition is built with QueueLimit = 0 unconditionally; a config attempt to
// set a queue is rejected at boot by RateLimitingOptions.Validate rather than
// silently building a queue nothing ever waits in.
public sealed class ReportConcurrencyLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public ReportConcurrencyLimiter(RateLimitingOptions options)
    {
        var policy = options.ReportsConcurrency;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(accountId =>
            RateLimitPartition.GetConcurrencyLimiter(accountId, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
    }

    // Non-waiting on purpose — see the remarks above. Never swap this for
    // AcquireAsync without changing the GLOSSARY + Help copy to match.
    public RateLimitLease Acquire(Guid accountId) => _limiter.AttemptAcquire(accountId);

    public void Dispose() => _limiter.Dispose();
}
