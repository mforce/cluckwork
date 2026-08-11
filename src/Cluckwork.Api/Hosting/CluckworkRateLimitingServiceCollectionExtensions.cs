namespace Cluckwork.Api.Hosting;

using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

internal static class CluckworkRateLimitingServiceCollectionExtensions
{
    // #347 review — role is OneShot for the operator verbs. Every limiter built
    // here exists to shape INBOUND HTTP, which a run-then-exit verb never serves,
    // so this whole section is serving-only machinery and its validation must be
    // scoped like one. It was not: a malformed CIDR or a nonzero
    // ReportsConcurrency:QueueLimit aborted `migrate`/`recover-admin` at service
    // registration — #331's shape again, and a stranger inconsistency than that,
    // because RateLimiting:TrustedProxies being EMPTY was already correctly
    // serving-only (#260) while the SAME key being malformed was hostile to every
    // role.
    public static CluckworkRateLimitingRegistration AddCluckworkRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        ProcessRole role = ProcessRole.Serving)
    {
        var rateLimiting = new RateLimitingOptions();
        IPNetwork[] trustedProxies;
        try
        {
            // The BINDING is inside the boundary too, not just the validation: a
            // non-numeric `RateLimiting:Login:PermitLimit` throws from Get<T>()
            // before any validator runs, and that aborted every verb just as
            // surely (#347 review). Same lesson as the OTLP section — scope
            // everything that can reject this configuration, or the next
            // unscoped part of it is the next #331.
            rateLimiting = configuration
                .GetSection(RateLimitingOptions.SectionName)
                .Get<RateLimitingOptions>() ?? new RateLimitingOptions();
            rateLimiting.Validate();
            trustedProxies = rateLimiting.ParseTrustedProxies();
        }
        catch (InvalidOperationException ex) when (role is ProcessRole.OneShot)
        {
            // Same degrade as the OTLP one: warn on stderr and carry on with
            // defaults that nothing in this process will consult, rather than
            // taking out an operational escape hatch over configuration for a
            // server this process is not going to be. Defaults, not the operator's
            // values — the operator's are the ones just declared unusable.
            Console.Error.WriteLine(
                $"warning: rate limiting not configured for this command — {ex.Message}");
            rateLimiting = new RateLimitingOptions();
            trustedProxies = [];
        }

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

                // #273 codex review (P1c) — a stable, alertable event for the AUTH
                // POLICY only (RateLimitingOptions.LoginPolicyName /
                // RefreshPolicyName): a 429 there is a brute-force/credential-
                // stuffing signal a deployment backend should be able to page on.
                // The client-errors policy (#217) guards log-pipeline VOLUME, not
                // a credential, so its rejections deliberately stay plain 429s
                // with no security event — see SecurityEvents.RateLimitRejected.
                //
                // Keyed on the endpoint's ATTACHED POLICY (via the
                // EnableRateLimitingAttribute metadata RequireRateLimiting sets),
                // not a hardcoded list of literal paths: AuthEndpoints attaches
                // LoginPolicyName to /auth/login AND /auth/step-up AND
                // /auth/change-password (all three verify a credential and must
                // share the brute-force budget), and the earlier path-list version
                // here only recognized /auth/login and /auth/refresh — a rejection
                // on step-up or change-password was silently invisible. Matching
                // the policy name means any FUTURE route that opts into the login
                // or refresh policy is covered automatically, with no second edit
                // required here.
                var policyName = context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                if (policyName == RateLimitingOptions.LoginPolicyName
                    || policyName == RateLimitingOptions.RefreshPolicyName)
                {
                    var rejectionLogger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Cluckwork.Api.Security.RateLimiting");
                    rejectionLogger.LogWarning("{SecurityEvent} client={ClientIp} path={Path}",
                        SecurityEvents.RateLimitRejected,
                        RateLimitKey.ForClient(context.HttpContext.Connection.RemoteIpAddress),
                        context.HttpContext.Request.Path.Value);
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
