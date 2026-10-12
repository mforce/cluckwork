using System.Collections.Concurrent;
using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Cluckwork.Api.IntegrationTests;

// #806 R2-01 — X-Forwarded-For from a peer outside RateLimiting:TrustedProxies is ignored, so
// every client behind that peer shares one address in each per-address limit. The first such
// request in a process logs a Warning naming the peer.
[Collection(IntegrationCollection.Name)]
public sealed class ForwardedHeadersIgnoredLoggingTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task ForwardedFor_FromAnUntrustedPeer_WarnsOnce_NamingThePeer()
    {
        var sink = new Sink();
        using var host = Host(sink);
        var client = host.CreateClient();

        await GetAsync(client, peer: "10.98.0.5", forwardedFor: "203.0.113.10");
        await GetAsync(client, peer: "10.98.0.6", forwardedFor: "203.0.113.11");

        var warning = Assert.Single(Events(sink));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Equal("\"10.98.0.5\"", warning.Properties["Peer"].ToString());
    }

    [Theory]
    [InlineData("10.99.0.2")]
    [InlineData("::ffff:10.99.0.2")]
    public async Task ForwardedFor_FromATrustedPeer_DoesNotWarn(string peer)
    {
        var sink = new Sink();
        using var host = Host(sink);

        await GetAsync(host.CreateClient(), peer, forwardedFor: "203.0.113.10");

        Assert.Empty(Events(sink));
    }

    // The framework also trusts KnownProxies; the app leaves that list empty today.
    [Fact]
    public async Task ForwardedFor_FromAKnownProxy_DoesNotWarn()
    {
        var sink = new Sink();
        using var host = Host(sink, services => services.PostConfigure<ForwardedHeadersOptions>(
            o => o.KnownProxies.Add(IPAddress.Parse("10.98.0.7"))));

        await GetAsync(host.CreateClient(), "10.98.0.7", forwardedFor: "203.0.113.10");

        Assert.Empty(Events(sink));
    }

    private WebApplicationFactory<Program> Host(Sink sink, Action<IServiceCollection>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:TrustedProxies:0", "10.99.0.0/24");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>();
                services.AddSingleton<ILogEventSink>(sink);
                configure?.Invoke(services);
            });
        });

    private static async Task GetAsync(HttpClient client, string peer, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Test-Remote", peer);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        (await client.SendAsync(request)).Dispose();
    }

    private static IEnumerable<LogEvent> Events(Sink sink) =>
        sink.Events.Where(e => e.Properties.TryGetValue("SecurityEvent", out var name)
            && name.ToString() == $"\"{SecurityEvents.ForwardedHeadersIgnored}\"");

    private sealed class Sink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
