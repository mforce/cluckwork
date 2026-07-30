namespace Cluckwork.Api.Hosting;

using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Cluckwork.Api.RateLimiting;

internal static class CluckworkRateLimitingServiceCollectionExtensions
{
    public static CluckworkRateLimitingRegistration AddCluckworkRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rateLimiting = configuration
            .GetSection(RateLimitingOptions.SectionName)
            .Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        rateLimiting.Validate();
        var trustedProxies = rateLimiting.ParseTrustedProxies();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = static async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(
                        MetadataName.RetryAfter,
                        out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await Results.Problem(
                        title: "Too many requests",
                        detail:
                            "Too many requests from this address. Try again later.",
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext);
            };

            AddFixedWindowByClientIp(
                limiter,
                RateLimitingOptions.LoginPolicyName,
                rateLimiting.Login);
            AddFixedWindowByClientIp(
                limiter,
                RateLimitingOptions.RefreshPolicyName,
                rateLimiting.Refresh);
            AddFixedWindowByClientIp(
                limiter,
                RateLimitingOptions.ClientErrorsPolicyName,
                rateLimiting.ClientErrors);
        });

        return new CluckworkRateLimitingRegistration(
            rateLimiting,
            trustedProxies);
    }

    private static void AddFixedWindowByClientIp(
        Microsoft.AspNetCore.RateLimiting.RateLimiterOptions limiter,
        string policyName,
        RateLimitingOptions.FixedWindow window) =>
        limiter.AddPolicy(policyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                RateLimitKey.ForClient(context.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = window.PermitLimit,
                    Window = TimeSpan.FromSeconds(window.WindowSeconds),
                    QueueLimit = 0
                }));
}

internal sealed record CluckworkRateLimitingRegistration(
    RateLimitingOptions Options,
    IPNetwork[] TrustedProxies);
