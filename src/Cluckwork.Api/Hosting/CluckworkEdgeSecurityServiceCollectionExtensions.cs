using Cluckwork.Application.Common;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Hosting;

internal static class CluckworkEdgeSecurityServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkEdgeSecurity(
        this IServiceCollection services,
        IReadOnlyCollection<System.Net.IPNetwork> trustedProxies)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = null;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in trustedProxies)
                options.KnownIPNetworks.Add(network);
        });

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        services.PostConfigure<HostFilteringOptions>(options =>
        {
            if (options.AllowedHosts.Contains("*"))
                return;

            var hosts = new List<string>(options.AllowedHosts);
            foreach (var loopback in new[] { "localhost", "127.0.0.1", "[::1]" })
                if (!hosts.Contains(loopback))
                    hosts.Add(loopback);
            options.AllowedHosts = hosts;
        });

        return services;
    }
}

internal static class ForwardedHeadersTrust
{
    // An untrusted peer's X-Forwarded-For is ignored, so every client behind that peer shares
    // its address in each per-address limit: login (#143) and the /mcp failure budget and
    // in-flight cap (#1225). The framework says so at Debug only. Checked here, before
    // UseForwardedHeaders, because only here is RemoteIpAddress still the socket peer.
    public static void UseForwardedHeadersWarningWhenIgnored(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        var warned = 0;
        app.Use((context, next) =>
        {
            if (warned == 0
                && context.Request.Headers.ContainsKey(options.ForwardedForHeaderName)
                && context.Connection.RemoteIpAddress is { } peer
                && !options.KnownIPNetworks.Any(n => n.Contains(peer))
                && Interlocked.Exchange(ref warned, 1) == 0)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Cluckwork.Api.Security.RateLimiting")
                    .LogWarning("{SecurityEvent} peer={Peer}: X-Forwarded-For was ignored because the peer is not in "
                        + "RateLimiting:TrustedProxies, so every client behind it shares one rate-limit address. "
                        + "Logged once per process.",
                        SecurityEvents.ForwardedHeadersIgnored,
                        peer.ToString());
            }

            return next(context);
        });
        app.UseForwardedHeaders();
    }
}
