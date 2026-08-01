namespace Cluckwork.Api.Endpoints.Reports;

using Cluckwork.Api.RateLimiting;
using Cluckwork.Infrastructure.Persistence;

// #311 — caps concurrently in-flight report queries per account so one
// authorized user firing many overlapping report requests cannot drive
// unbounded DB/CPU cost; excess work returns a retryable 429 rather than
// queueing or running unbounded. Registered as a filter on the reports route
// group (ReportEndpoints.MapReportEndpoints) rather than via the global
// RequireRateLimiting policy pipeline — see ReportConcurrencyLimiter's remarks
// for why. TenantContext is resolved from RequestServices (the scoped,
// per-request provider) rather than constructor-injected: AddEndpointFilter<T>
// constructs the filter once, from the app's ROOT service provider, at
// endpoint-build time, so a constructor-injected scoped service would either
// fail to resolve or capture a stale instance.
public sealed class ReportConcurrencyLimitFilter(ReportConcurrencyLimiter limiter) : IEndpointFilter
{
    private const string RetryAfterSeconds = "1";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        if (!tenant.IsResolved)
            return await next(context); // unauthenticated — the handler itself returns 401

        using var lease = limiter.Acquire(tenant.AccountId);
        if (!lease.IsAcquired)
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
