namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

// #344 — HSTS and HTTPS redirection, asserted over a REAL TLS connection.
//
// Every other assertion about these two middlewares in this suite runs against
// the in-process TestServer, which has no transport: it "speaks" a scheme
// rather than having one, and `X-Forwarded-Proto: https` from a synthesized
// peer is what stands in for HTTPS. That proves the configuration is wired; it
// cannot observe the two properties that decide whether a browser is handed a
// one-year commitment to plaintext refusal:
//
//   * `HstsOptions.ExcludedHosts` — whose defaults (localhost, 127.0.0.1,
//     [::1]) the earlier suites have to CLEAR to see the header at all, which
//     means the exclusion itself is never exercised; and
//   * `UseHttpsRedirection`'s port resolution from `IServerAddressesFeature`,
//     which needs a bound HTTPS address. The earlier suites pin `https_port`
//     instead, so the production code path never runs.
//
// The harness here binds two real Kestrel listeners on loopback — one plain
// HTTP, one TLS with a runtime-generated certificate (TestTlsCertificate) — and
// dials them with a client whose ConnectCallback sends every hostname to
// 127.0.0.1. So the request URI's host is free, while the socket stays on
// loopback: `https://cluckwork.test:<port>` is a genuine HTTPS request to a
// host HSTS does not exclude, and `https://localhost:<port>` is a genuine HTTPS
// request to one it does. Nothing here clears ExcludedHosts, which is the point
// — the exclusion is an assertion target, not an obstacle.
public sealed class TlsKestrelFactory : CluckworkWebApplicationFactory
{
    // Must run before anything touches Services (which starts the host).
    // Port 0 twice: the OS picks both, so two fixtures can never collide.
    public TlsKestrelFactory() => UseKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(TestTlsCertificate.Certificate));
    });

    // Deliberately NOT `https_port`: reading the bound port back out of the
    // server is what makes the redirect assertion cover the resolution path
    // `UseHttpsRedirection` uses in production.
    public int HttpPort => PortOf("http://");

    public int HttpsPort => PortOf("https://");

    private int PortOf(string schemePrefix)
    {
        var addresses = Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        return new Uri(addresses.Single(a => a.StartsWith(schemePrefix, StringComparison.Ordinal))).Port;
    }

    public Uri Url(string scheme, string host, string path) =>
        new($"{scheme}://{host}:{(scheme == "https" ? HttpsPort : HttpPort)}{path}");

    // Trusts EXACTLY the generated certificate (compared by SHA-256 hash, not
    // by a blanket `=> true`), so a test cannot pass against whatever else might
    // answer on a loopback port. The ConnectCallback is what frees the URI host
    // from DNS while keeping the connection on loopback.
    public HttpClient CreateTlsClient(bool allowAutoRedirect = false) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        TestTlsCertificate.Sha256Thumbprint,
                        StringComparison.OrdinalIgnoreCase),
            },
        }, disposeHandler: true);
}

public sealed class HstsOverRealTlsTests(TlsKestrelFactory factory) : IClassFixture<TlsKestrelFactory>
{
    private const string Probe = "/health/live";

    private async Task<HttpResponseMessage> GetAsync(string scheme, string host)
        => await factory.CreateTlsClient().GetAsync(factory.Url(scheme, host, Probe));

    private static string? Hsts(HttpResponseMessage response)
        => response.Headers.TryGetValues("Strict-Transport-Security", out var values)
            ? values.Single()
            : null;

    // The positive direction: a real TLS request to a host that is not excluded
    // gets the exact commitment CluckworkEdgeSecurityServiceCollectionExtensions
    // configures. Asserted as the complete value, not a substring — a max-age
    // that silently grew or an includeSubDomains that silently appeared are both
    // changes to a one-way door and must be deliberate.
    [Fact]
    public async Task Hsts_is_emitted_over_real_tls_to_a_non_excluded_host()
    {
        var response = await GetAsync("https", TestTlsCertificate.PublicHost);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("max-age=31536000; includeSubDomains", Hsts(response));
        // And the request that carries it was not itself redirected — the
        // redirect loop #144 exists to avoid, now observed over a real
        // connection instead of a forwarded header.
        Assert.Null(response.Headers.Location);
    }

    // The direction with the irreversible consequence, and the one no in-process
    // test can reach: the connection really is HTTPS, so scheme cannot be the
    // reason the header is withheld — only ExcludedHosts can be. Delete those
    // defaults and a developer hitting https://localhost is handed a year of
    // plaintext refusal for a host that will never have a public certificate.
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task Hsts_is_withheld_over_real_tls_from_an_excluded_loopback_host(string host)
    {
        var response = await GetAsync("https", host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Hsts(response));
    }

    // The scheme gate, over a real plaintext socket rather than an absent
    // X-Forwarded-Proto, on the same host that DOES get the header over TLS —
    // so the host is held constant and scheme is the only variable.
    [Fact]
    public async Task Hsts_is_withheld_over_real_plain_http_from_the_same_host()
    {
        var response = await GetAsync("http", TestTlsCertificate.PublicHost);

        Assert.Null(Hsts(response));
    }

    // #344's second half: the redirect target's port comes from
    // IServerAddressesFeature, which only exists when an HTTPS address is really
    // bound. Asserting the whole Location — not just the status — is what pins
    // the port resolution rather than the mere fact of a redirect.
    [Fact]
    public async Task Https_redirection_sends_plain_http_to_the_bound_https_port()
    {
        var response = await GetAsync("http", TestTlsCertificate.PublicHost);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(
            factory.Url("https", TestTlsCertificate.PublicHost, Probe),
            response.Headers.Location);
    }

    // Guards the harness itself rather than the product: if the client ever
    // stopped negotiating TLS (a ConnectCallback that quietly dialled the plain
    // port, a scheme typo), every assertion above would still be green — the
    // positive one would fail, but the three withheld-header ones would pass for
    // the wrong reason. Two real listeners on two different ports is the
    // premise they all rest on.
    [Fact]
    public void The_harness_binds_two_distinct_real_listeners()
    {
        Assert.NotEqual(factory.HttpPort, factory.HttpsPort);
        Assert.NotEqual(0, factory.HttpsPort);
    }
}
