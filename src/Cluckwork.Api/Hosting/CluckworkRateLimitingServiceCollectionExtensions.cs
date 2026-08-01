namespace Cluckwork.Api.Hosting;

using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

                // #273 — a stable, alertable event for the two AUTH policies only
                // (login/refresh): a 429 there is a brute-force/credential-stuffing
                // signal a deployment backend should be able to page on. The
                // client-errors policy (#217) guards log-pipeline VOLUME, not a
                // credential, so its rejections stay plain 429s with no security
                // event — see SecurityEvents.RateLimitRejected.
                var path = context.HttpContext.Request.Path;
                if (path.StartsWithSegments("/api/v1/auth/login")
                    || path.StartsWithSegments("/api/v1/auth/refresh"))
                {
                    var rejectionLogger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Cluckwork.Api.Security.RateLimiting");
                    rejectionLogger.LogWarning("{SecurityEvent} client={ClientIp} path={Path}",
                        SecurityEvents.RateLimitRejected,
                        RateLimitKey.ForClient(context.HttpContext.Connection.RemoteIpAddress),
                        path.Value);
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

        // #311 — account-scoped, not IP-scoped, so it lives outside the
        // AddRateLimiter() policy set above (see ReportConcurrencyLimiter for why).
        // A factory registration (not an instance) so the container disposes it
        // on shutdown.
        services.AddSingleton(_ => new ReportConcurrencyLimiter(rateLimiting));

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
