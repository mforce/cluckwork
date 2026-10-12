using System.Net;
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
        // #260: an empty list trusts no proxy. The framework middleware trusts every peer when
        // its trust lists are empty, and ForwardedHeaders_Enabled=true adds that middleware
        // without us, so forwarding is switched off rather than only not added.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = trustedProxies.Count == 0
                ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
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
    // With forwarding off (no trusted proxy), neither middleware is added: the warning would
    // otherwise report every forwarded request.
    //
    // A peer outside a non-empty list has its X-Forwarded-For ignored, so every client behind
    // it shares one address in each per-address limit: login (#143) and the /mcp failure budget
    // and in-flight cap (#1225). The framework says so at Debug only. Checked before
    // UseForwardedHeaders, because only there is RemoteIpAddress still the socket peer.
    public static void UseTrustedForwardedHeaders(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        if (options.ForwardedHeaders == ForwardedHeaders.None)
            return;

        var warned = 0;
        app.Use((context, next) =>
        {
            if (warned == 0
                && context.Request.Headers.ContainsKey(options.ForwardedForHeaderName)
                && context.Connection.RemoteIpAddress is { } peer
                && !IsKnown(options, peer)
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

    // ForwardedHeadersMiddleware.CheckKnownAddress (aspnetcore v10.0.0).
    private static bool IsKnown(ForwardedHeadersOptions options, IPAddress peer) =>
        (peer.IsIPv4MappedToIPv6 && IsKnown(options, peer.MapToIPv4()))
        || options.KnownProxies.Contains(peer)
        || options.KnownIPNetworks.Any(n => n.Contains(peer));
}
