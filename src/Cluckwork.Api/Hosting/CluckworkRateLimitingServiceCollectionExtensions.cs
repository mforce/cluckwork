using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Cluckwork.Api.RateLimiting;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;

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

                // #806 — an MCP client reads JSON-RPC, and oauth-api counts per token, not per
                // address. The request's id is unknown here, so the error carries none.
                if (McpEndpoint.Of(context.HttpContext) is not null)
                {
                    await Results.Json(new
                        {
                            jsonrpc = "2.0",
                            id = (object?)null,
                            error = new { code = -32000, message = "Too many requests on this connection. Try again later." },
                        }, statusCode: StatusCodes.Status429TooManyRequests)
                        .ExecuteAsync(context.HttpContext);
                    return;
                }

                await Results.Problem(
                        title: "Too many requests",
                        detail: policyName == RateLimitingOptions.OAuthApiPolicyName
                            ? "Too many requests with this access token. Try again later."
                            : "Too many requests from this address. Try again later.",
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
}

internal sealed record CluckworkRateLimitingRegistration(
    RateLimitingOptions Options,
    IPNetwork[] TrustedProxies);
