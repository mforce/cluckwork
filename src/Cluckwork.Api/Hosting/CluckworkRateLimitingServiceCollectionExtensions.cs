using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Cluckwork.Api.Hosting;

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

                // #273 codex review (P1c), #1164 — a stable, alertable event for every
                // policy guarding a credential or an OAuth surface: a 429 there is a
                // brute-force or flooding signal a deployment backend should page on. The
                // client-errors policy (#217) guards log-pipeline VOLUME, not a credential,
                // so its rejections stay plain 429s — see SecurityEvents.RateLimitRejected.
                //
                // Keyed on the endpoint's ATTACHED POLICY (the EnableRateLimitingAttribute
                // metadata RequireRateLimiting sets), never on paths: a path list missed
                // step-up and change-password, and a login/refresh allow-list missed the
                // four OAuth policies (#1164). Path carries no query string; ClientIp is
                // logged, never the oauth-api partition key, which hashes a bearer.
                var policyName = context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                if (policyName is not null && policyName != RateLimitingOptions.ClientErrorsPolicyName)
                {
                    var rejectionLogger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Cluckwork.Api.Security.RateLimiting");
                    rejectionLogger.LogWarning("{SecurityEvent} policy={Policy} client={ClientIp} path={Path}",
                        SecurityEvents.RateLimitRejected,
                        policyName,
                        RateLimitKey.ForClient(context.HttpContext.Connection.RemoteIpAddress),
                        context.HttpContext.Request.Path.Value);
                }

                if (policyName == RateLimitingOptions.OAuthApiPolicyName)
                {
                    await WriteJsonRpcTooManyRequestsAsync(context.HttpContext.Response);
                    return;
                }

                await Results.Problem(
                        title: "Too many requests",
                        detail:
                            "Too many requests from this address. Try again later.",
                        statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext);
            };

            limiter.AddPolicy<string>(
                RateLimitingOptions.LoginPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.LoginPolicyName,
                    rateLimiting.Login.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.Login.WindowSeconds)));
            limiter.AddPolicy<string>(
                RateLimitingOptions.RefreshPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.RefreshPolicyName,
                    rateLimiting.Refresh.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.Refresh.WindowSeconds)));
            limiter.AddPolicy<string>(
                RateLimitingOptions.ClientErrorsPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.ClientErrorsPolicyName,
                    rateLimiting.ClientErrors.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.ClientErrors.WindowSeconds)));
            limiter.AddPolicy<string>(
                RateLimitingOptions.OAuthRegisterPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.OAuthRegisterPolicyName,
                    rateLimiting.OAuthRegister.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.OAuthRegister.WindowSeconds)));
            limiter.AddPolicy<string>(
                RateLimitingOptions.OAuthTokenPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.OAuthTokenPolicyName,
                    rateLimiting.OAuthToken.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.OAuthToken.WindowSeconds)));
            limiter.AddPolicy<string>(
                RateLimitingOptions.OAuthAuthorizePolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.OAuthAuthorizePolicyName,
                    rateLimiting.OAuthAuthorize.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.OAuthAuthorize.WindowSeconds)));
            // UseRateLimiter runs before UseAuthentication, so the key is the raw bearer,
            // not the principal it will authenticate to.
            limiter.AddPolicy<string>(
                RateLimitingOptions.OAuthApiPolicyName,
                new DistributedFixedWindowPolicy(
                    RateLimitingOptions.OAuthApiPolicyName,
                    rateLimiting.OAuthApi.PermitLimit,
                    TimeSpan.FromSeconds(rateLimiting.OAuthApi.WindowSeconds),
                    RateLimitKey.ForBearer));
        });

        // #311/#545 — account-scoped report concurrency cap. Registered separately
        // in Program.cs via AddCluckworkReportConcurrencyCap (it needs the shared
        // Redis namespace and the internal lease types), not here.

        return new CluckworkRateLimitingRegistration(
            rateLimiting,
            trustedProxies);
    }

    // #806 — oauth-api serves /mcp, whose client reads a JSON-RPC error. The body is unread
    // here, so the request id is unknown.
    internal static Task WriteJsonRpcTooManyRequestsAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        return response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id = (string?)null,
            error = new { code = -32000, message = "Too many requests. Try again later." },
        });
    }
}

internal static class OAuthApiAddressLimits
{
    // Before UseRateLimiter, so a refused address spends no per-token permit and
    // authenticates nothing. Two limits, both per client address:
    // - the failure budget, shared across replicas: only a presented bearer refused with 401
    //   spends it, so a valid call, a scope 403, a per-token 429 and a client's tokenless
    //   discovery probe spend nothing;
    // - bearer authentications in flight, which bounds the lookups admitted before their
    //   refusals reach the failure budget. UseOAuthApiInFlightRelease gives the permit back once
    //   the credential gates have decided, so a valid call's body and tool work hold none.
    public static void UseOAuthApiAddressLimits(this IApplicationBuilder app)
    {
        var budget = new FailedAuthenticationBudget(
            RateLimitingOptions.OAuthApiFailuresKeyPrefix,
            RateLimitingOptions.OAuthApiFailuresPerAddress,
            RateLimitingOptions.OAuthApiFailuresWindow);
        // Per process on purpose: requests in flight are this instance's own work, so each
        // replica bounds its own. It is not one of #271's combined budgets.
        var inFlight = PartitionedRateLimiter.Create<string, string>(address =>
            RateLimitPartition.GetConcurrencyLimiter(address, _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = RateLimitingOptions.OAuthApiBearersInFlightPerAddress,
                QueueLimit = 0,
            }));
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(inFlight.Dispose);

        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                != RateLimitingOptions.OAuthApiPolicyName)
            {
                await next(context);
                return;
            }

            if (await budget.RefusalAsync(context) is { } retryAfter)
            {
                await RefuseAsync(context, RateLimitingOptions.OAuthApiFailuresKeyPrefix, retryAfter);
                return;
            }

            if (!RateLimitKey.HasBearer(context.Request))
            {
                await next(context);
                return;
            }

            // Disposed here on every path the release below does not reach: a refusal, an
            // exception, a cancellation. A lease's Dispose releases its permit once.
            using var lease = inFlight.AttemptAcquire(RateLimitKey.ForClient(context.Connection.RemoteIpAddress));
            if (!lease.IsAcquired)
            {
                await RefuseAsync(context, RateLimitingOptions.OAuthApiInFlightPolicyName, TimeSpan.FromSeconds(1));
                return;
            }

            context.Features.Set(new InFlightAuthentication(lease));
            await next(context);
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                await budget.RecordAsync(context);
        });
    }

    // After the credential gates, before authorization, body reading and tool work.
    public static void UseOAuthApiInFlightRelease(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Features.Get<InFlightAuthentication>()?.Lease.Dispose();
            return next(context);
        });

    private sealed record InFlightAuthentication(RateLimitLease Lease);

    private static Task RefuseAsync(HttpContext context, string policy, TimeSpan retryAfter)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Cluckwork.Api.Security.RateLimiting")
            .LogWarning("{SecurityEvent} policy={Policy} client={ClientIp} path={Path}",
                SecurityEvents.RateLimitRejected,
                policy,
                RateLimitKey.ForClient(context.Connection.RemoteIpAddress),
                context.Request.Path.Value);
        context.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return CluckworkRateLimitingServiceCollectionExtensions.WriteJsonRpcTooManyRequestsAsync(context.Response);
    }
}

internal sealed record CluckworkRateLimitingRegistration(
    RateLimitingOptions Options,
    IPNetwork[] TrustedProxies);
