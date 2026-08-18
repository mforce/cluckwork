namespace Cluckwork.Api.Endpoints.Reports;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.RateLimiting;

// #311/#545 — caps concurrently in-flight report queries per account so one
// account firing many overlapping report requests cannot drive unbounded DB/CPU
// cost; excess work returns a retryable 429 rather than queueing or running
// unbounded. #545 moved the cap onto the shared lease backends so N replicas
// enforce ONE combined per-account count. Registered as a filter on the reports
// route group (ReportEndpoints.MapReportEndpoints). TenantContext is resolved
// from RequestServices (the scoped, per-request provider) rather than
// constructor-injected: AddEndpointFilter<T> constructs the filter once, from the
// app's ROOT service provider, so a constructor-injected scoped service would
// capture a stale instance. The limiter is a singleton — safe to inject.
public sealed class ReportConcurrencyLimitFilter(DistributedReportConcurrencyLimiter limiter) : IEndpointFilter
{
    private const string RetryAfterSeconds = "1";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        // No account to partition by, so there is nothing to meter — fall through
        // and let the handler reject it (unauthenticated, or an authenticated JWT
        // with no usable account_id claim).
        if (!tenant.IsResolved)
            return await next(context);

        await using var permit = await limiter.AcquireAsync(
            tenant.AccountId, context.HttpContext.RequestAborted);
        if (permit is null)
        {
            context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
            return Results.Problem(
                title: "Too many requests",
                detail: "Too many concurrent report requests for this account. Try again shortly.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context);
    }
}
