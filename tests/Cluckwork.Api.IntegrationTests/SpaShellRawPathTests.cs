namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

// #874 review (local Codex pass) — a raw "GET //index.html" request-target.
// TestServer's in-memory transport routes every request through System.Uri,
// which treats a leading "//" as an RFC 3986 network-path reference (a NEW
// authority) rather than a literal path segment — measured directly:
// HttpClient.GetAsync("//index.html") AND TestServer's own
// Server.CreateRequest("//index.html") both come back 400 on a host mismatch,
// neither ever reaching the app with the path the review is actually about.
// This binds a real loopback Kestrel listener, the same pattern
// HstsOverRealTlsTests (#344) uses for the same reason, and writes the request
// line by hand over a raw socket to prove what a real client's request
// actually carries.
public sealed class RawPathKestrelFactory : CluckworkWebApplicationFactory
{
    private readonly string _webRoot = Path.Combine(
        Path.GetTempPath(), "cluckwork-rawpath-" + Guid.NewGuid().ToString("N"));

    public RawPathKestrelFactory()
    {
        // Must run before anything touches Services (which starts the host) —
        // same constraint TlsKestrelFactory documents.
        UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), StaticCachingFactory.IndexHtml);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseWebRoot(_webRoot);
    }

    public int Port => new Uri(Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses
        .Single(a => a.StartsWith("http://", StringComparison.Ordinal))).Port;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            try { Directory.Delete(_webRoot, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class SpaShellRawPathTests(RawPathKestrelFactory factory)
    : IClassFixture<RawPathKestrelFactory>
{
    private async Task<(int Status, string Raw)> SendRawRequestAsync(string requestLine)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, factory.Port);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = Encoding.ASCII.GetBytes(
            $"{requestLine} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var raw = await reader.ReadToEndAsync();
        var statusLine = raw[..raw.IndexOf("\r\n", StringComparison.Ordinal)];
        return (int.Parse(statusLine.Split(' ')[1]), raw);
    }

    [Fact]
    public async Task Double_leading_slash_never_serves_the_untemplated_document()
    {
        var (status, raw) = await SendRawRequestAsync("GET //index.html");

        // Two acceptable outcomes: a 200 that DOES carry the nonce meta (served
        // by MapFallback, exactly like any other client-side route falls
        // through to it), or anything that is not a 200 HTML document. What
        // must never happen — and did, before IndexHtmlHidingFileProvider —
        // is a 200 whose body is the raw build artifact with no nonce meta at
        // all: UseStaticFiles's PhysicalFileProvider used to resolve this exact
        // request to the on-disk index.html and serve it with an ETag, the
        // validator #873's own design forbids on this document.
        if (status == 200)
            Assert.Contains("csp-nonce", raw);
        else
            Assert.NotEqual(200, status);
    }
}
