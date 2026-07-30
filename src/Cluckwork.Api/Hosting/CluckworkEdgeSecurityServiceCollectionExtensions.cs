namespace Cluckwork.Api.Hosting;

using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;

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
