using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Infrastructure.RateLimiting;

// Refused credentials per client address, counted in the shared IFixedWindowCounter (#543)
// like every other limiter here, so replicas share one count (#271). A caller checks the
// budget before authenticating and records only a refusal, so a valid token's calls never
// spend it.
//
// ponytail: check-then-record lets concurrent requests from one address overshoot the
// limit by their number in flight; an atomic reserve-and-refund would close that.
public sealed class FailedAuthenticationBudget(string keyPrefix, int limit, TimeSpan window)
{
    // Null while the address may try; otherwise the time until its window rolls over.
    public async ValueTask<TimeSpan?> RefusalAsync(HttpContext context)
    {
        var spent = await Counter(context).CountAsync(Key(context), window, context.RequestAborted);
        return spent.Count >= limit ? spent.Remaining : null;
    }

    // Not the request's token: a caller that hangs up after its refusal still spends one.
    public async ValueTask RecordAsync(HttpContext context) =>
        await Counter(context).IncrementAsync(Key(context), window, CancellationToken.None);

    private string Key(HttpContext context) => $"{keyPrefix}:{RateLimitKey.ForClient(context.Connection.RemoteIpAddress)}";

    private static IFixedWindowCounter Counter(HttpContext context) =>
        context.RequestServices.GetRequiredService<IFixedWindowCounter>();
}
