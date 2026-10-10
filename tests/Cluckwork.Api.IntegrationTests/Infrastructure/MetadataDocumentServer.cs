using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cluckwork.Infrastructure.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #1148 — a loopback TLS server that answers whatever a test scripts, so every fetcher
// rule is proven against a real connection, never a real internet host. Requests reach
// it only through FakeNetwork's dial, which maps whichever address the fetcher vetted to
// this port and records it.
internal sealed class MetadataDocumentServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public Func<HttpContext, Task> Respond { get; set; } = context => context.Response.WriteAsync("");

    public ConcurrentQueue<HttpRequestSnapshot> Requests { get; } = new();

    public int Port { get; }

    private MetadataDocumentServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public static async Task<MetadataDocumentServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(TestTlsCertificate.Certificate)));
        var app = builder.Build();
        MetadataDocumentServer? server = null;
        app.Run(async context =>
        {
            server!.Requests.Enqueue(new(context.Request.Path, context.Request.Headers.ToDictionary(
                header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await server.Respond(context);
        });
        await app.StartAsync();
        var port = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return server = new MetadataDocumentServer(app, port);
    }

    // A valid public-client document naming itself.
    public static string Document(string clientId, string redirectUri = "https://app.test/callback", string name = "Doc Client") =>
        $$"""{"client_id":"{{clientId}}","client_name":"{{name}}","redirect_uris":["{{redirectUri}}"],"grant_types":["authorization_code"],"response_types":["code"],"token_endpoint_auth_method":"none"}""";

    public Func<HttpContext, Task> Serves(string body, string contentType = "application/json", int status = 200,
        string? cacheControl = null) => async context =>
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        if (cacheControl is not null) context.Response.Headers.CacheControl = cacheControl;
        await context.Response.WriteAsync(body);
    };

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

internal sealed record HttpRequestSnapshot(string Path, IReadOnlyDictionary<string, string> Headers);

// The fetcher's DNS and sockets, scripted. Resolve answers from Answer; Dial records the
// endpoint the fetcher chose and connects to the local server whatever the address.
internal sealed class FakeNetwork(MetadataDocumentServer server)
{
    // A public address that is never dialled for real: Dial sends every connection to
    // the loopback server.
    public static readonly IPAddress Public = IPAddress.Parse("8.8.4.4");

    public Func<string, IPAddress[]> Answer { get; set; } = _ => [Public];

    public ConcurrentQueue<string> Resolves { get; } = new();

    public ConcurrentQueue<IPEndPoint> Dials { get; } = new();

    public Func<IPEndPoint, CancellationToken, Task>? BeforeDial { get; set; }

    public FetchTransport Transport => new(
        (host, _) =>
        {
            Resolves.Enqueue(host);
            return Task.FromResult(Answer(host));
        },
        async (endpoint, ct) =>
        {
            Dials.Enqueue(endpoint);
            if (BeforeDial is not null) await BeforeDial(endpoint, ct);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port, ct);
            return client.GetStream();
        },
        // Exactly the suite's test certificate, by hash, never a blanket true.
        (_, certificate, _, _) => certificate is not null
            && certificate.GetCertHashString(HashAlgorithmName.SHA256) == TestTlsCertificate.Sha256Thumbprint);

    public ClientMetadataFetcher Fetcher(IReadOnlySet<string>? privateHosts = null, TimeSpan? deadline = null) =>
        new(Transport, privateHosts ?? new HashSet<string>(), deadline);
}
