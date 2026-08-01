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
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
    }

    public RateLimitLease Acquire(Guid accountId) => _limiter.AttemptAcquire(accountId);

    public void Dispose() => _limiter.Dispose();
}
