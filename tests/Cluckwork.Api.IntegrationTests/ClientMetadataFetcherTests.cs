using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.OAuth;
using Microsoft.AspNetCore.Http;

namespace Cluckwork.Api.IntegrationTests;

// #1148 — the metadata document fetcher is a server-side request forgery surface. Each
// rule is proven against a real TLS connection to a loopback server, reached only through
// a scripted resolver and dialler; no test touches a real host. Needs no database.
public sealed class ClientMetadataFetcherTests : IAsyncLifetime
{
    private const string Url = "https://app.test/oauth/client.json";
    private MetadataDocumentServer _server = null!;
    private FakeNetwork _network = null!;

    public async Task InitializeAsync()
    {
        _server = await MetadataDocumentServer.StartAsync();
        _server.Respond = _server.Serves(MetadataDocumentServer.Document(Url));
        _network = new FakeNetwork(_server);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task PublicHost_IsFetched_OverTheVettedAddress()
    {
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(MetadataDocumentServer.Document(Url), Encoding.UTF8.GetString(result.Value.Json));
        Assert.Equal([new IPEndPoint(FakeNetwork.Public, 443)], _network.Dials);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2620:4f:8000::1")]
    [InlineData("192.175.48.1")]
    [InlineData("5f00::1")]
    [InlineData("100::1")]
    public async Task SpecialUseAddress_IsRefused_BeforeAnyConnection(string address)
    {
        _network.Answer = _ => [IPAddress.Parse(address)];
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.AddressRefused, result.Error.Code);
        Assert.Empty(_network.Dials);
    }

    // A rebinding host answers one public and one private address; any special-use answer
    // refuses the host, so the order the resolver returns them in cannot matter.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedAnswer_IsRefused_WhicheverAddressComesFirst(bool publicFirst)
    {
        var refused = IPAddress.Parse("10.0.0.5");
        _network.Answer = _ => publicFirst ? [FakeNetwork.Public, refused] : [refused, FakeNetwork.Public];
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.AddressRefused, result.Error.Code);
        Assert.Empty(_network.Dials);
    }

    // DNS rebinding: the second answer for the name is private. The fetcher resolves once
    // and connects to the address it vetted, so the second answer is never consulted.
    [Fact]
    public async Task Connection_IsPinnedToTheVettedAddress()
    {
        var answers = 0;
        _network.Answer = _ => Interlocked.Increment(ref answers) == 1 ? [FakeNetwork.Public] : [IPAddress.Loopback];
        _network.BeforeDial = (endpoint, _) => endpoint.Address.Equals(FakeNetwork.Public)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"dialled {endpoint}, which was never vetted");
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Single(_network.Resolves);
        Assert.Equal([new IPEndPoint(FakeNetwork.Public, 443)], _network.Dials);
    }

    // OAuth:ClientMetadata:PrivateHosts lets a named host reach a private network, never
    // loopback or link-local, and never lends that to another name.
    [Theory]
    [InlineData("fixture.test", "172.18.0.5", true)]
    [InlineData("fixture.test", "10.0.0.7", true)]
    [InlineData("fixture.test", "127.0.0.1", false)]
    [InlineData("fixture.test", "169.254.169.254", false)]
    [InlineData("fixture.test", "::ffff:172.18.0.5", false)]
    [InlineData("evil.fixture.test", "172.18.0.5", false)]
    [InlineData("other.test", "172.18.0.5", false)]
    public async Task PrivateHosts_AdmitsOnlyTheNamedHost_OnAPrivateNetwork(string host, string address, bool admitted)
    {
        var url = $"https://{host}/oauth/client.json";
        _server.Respond = _server.Serves(MetadataDocumentServer.Document(url));
        _network.Answer = _ => [IPAddress.Parse(address)];
        using var fetcher = _network.Fetcher(new HashSet<string> { "fixture.test" });

        var result = await fetcher.FetchAsync(new Uri(url), CancellationToken.None);

        Assert.Equal(admitted, result.IsSuccess);
        if (!admitted) Assert.Equal(ClientMetadataFetcher.AddressRefused, result.Error.Code);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        _server.Respond = context =>
        {
            if (context.Request.Path == "/moved")
                return _server.Serves(MetadataDocumentServer.Document(Url))(context);
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = "https://app.test/moved";
            return Task.CompletedTask;
        };
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.Redirected, result.Error.Code);
        Assert.Single(_server.Requests);
    }

    [Theory]
    [InlineData(203)]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task OnlyOk_IsAccepted(int status)
    {
        _server.Respond = _server.Serves(MetadataDocumentServer.Document(Url), status: status);
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.BadStatus, result.Error.Code);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("application/jsonp")]
    public async Task OtherContentType_IsRefused(string contentType)
    {
        _server.Respond = _server.Serves(MetadataDocumentServer.Document(Url), contentType);
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.BadContentType, result.Error.Code);
    }

    [Fact]
    public async Task JsonWithACharset_IsAccepted()
    {
        _server.Respond = _server.Serves(MetadataDocumentServer.Document(Url), "application/json; charset=utf-8");
        using var fetcher = _network.Fetcher();

        Assert.True((await fetcher.FetchAsync(new Uri(Url), CancellationToken.None)).IsSuccess);
    }

    // Chunked, so no Content-Length warns the client: the cap holds while streaming.
    [Theory]
    [InlineData(ClientMetadataFetcher.MaxBodyBytes, true)]
    [InlineData(ClientMetadataFetcher.MaxBodyBytes + 1, false)]
    public async Task StreamedBody_IsCappedWhileReading(int size, bool accepted)
    {
        _server.Respond = async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(new byte[size / 2]);
            await context.Response.Body.FlushAsync();
            await context.Response.Body.WriteAsync(new byte[size - size / 2]);
        };
        using var fetcher = _network.Fetcher();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(accepted, result.IsSuccess);
        if (accepted) Assert.Equal(size, result.Value.Json.Length);
        else Assert.Equal(ClientMetadataFetcher.TooLarge, result.Error.Code);
    }

    // A declared length over the cap is refused from the headers; the body never arrives.
    [Fact]
    public async Task DeclaredLength_OverTheCap_IsRefusedBeforeReading()
    {
        _server.Respond = async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 10_000_000;
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        };
        using var fetcher = _network.Fetcher(deadline: TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.TooLarge, result.Error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed} for a body it should refuse");
    }

    // A body cut short after the headers is a failed fetch, not a server error.
    [Fact]
    public async Task InterruptedBody_IsUnreachable()
    {
        _server.Respond = async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 100;
            await context.Response.Body.WriteAsync("{}"u8.ToArray());
            await context.Response.Body.FlushAsync();
            context.Abort();
        };
        using var fetcher = _network.Fetcher();

        Result<FetchedDocument>? result = null;
        var thrown = await Record.ExceptionAsync(async () => result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None));

        Assert.True(thrown is null, $"the fetch threw {thrown?.GetType().Name}");
        Assert.Equal(ClientMetadataFetcher.Unreachable, result!.Error.Code);
    }

    // A server that sends its headers at once and then trickles the body cannot hold the
    // request past the deadline.
    [Fact]
    public async Task SlowBody_StopsAtTheDeadline()
    {
        _server.Respond = async context =>
        {
            context.Response.ContentType = "application/json";
            for (var i = 0; i < 100 && !context.RequestAborted.IsCancellationRequested; i++)
            {
                await context.Response.Body.WriteAsync(" "u8.ToArray());
                await context.Response.Body.FlushAsync();
                await Task.Delay(200, context.RequestAborted);
            }
        };
        using var fetcher = _network.Fetcher(deadline: TimeSpan.FromMilliseconds(800));
        var clock = Stopwatch.StartNew();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.TimedOut, result.Error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the slow body held the fetch for {clock.Elapsed}");
    }

    [Fact]
    public async Task SlowConnect_StopsAtTheDeadline()
    {
        _network.BeforeDial = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        using var fetcher = _network.Fetcher(deadline: TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        var result = await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        Assert.Equal(ClientMetadataFetcher.TimedOut, result.Error.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the connect held the fetch for {clock.Elapsed}");
    }

    // The fetch carries no cookie, credential or other ambient state, and says who it is.
    [Fact]
    public async Task Request_CarriesNoCookie_AndAnHonestUserAgent()
    {
        _server.Respond = async context =>
        {
            context.Response.Headers.SetCookie = "session=abc; Path=/";
            await _server.Serves(MetadataDocumentServer.Document(Url))(context);
        };
        using var fetcher = _network.Fetcher();

        await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);
        await fetcher.FetchAsync(new Uri(Url), CancellationToken.None);

        var second = _server.Requests.Last().Headers;
        Assert.Equal(["Accept", "Host", "User-Agent"], second.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ClientMetadataFetcher.UserAgent, second["User-Agent"]);
        Assert.Equal("application/json", second["Accept"]);
    }

    [Fact]
    public void Handler_FollowsNoRedirect_UsesNoProxy_KeepsNoCookies()
    {
        using var handler = ClientMetadataFetcher.CreateHandler(FetchTransport.System, new HashSet<string>());

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Null(FetchTransport.System.CertificateValidation);
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData("max-age=600", 600)]
    [InlineData("max-age=10", 300)]
    [InlineData("max-age=0", 300)]
    [InlineData("max-age=100000000", 86400)]
    [InlineData("no-store", 300)]
    [InlineData("no-cache, max-age=7200", 300)]
    public void Lifetime_FollowsCacheControl_WithinTheBounds(string? header, int seconds)
    {
        var cacheControl = header is null ? null : CacheControlHeaderValue.Parse(header);

        Assert.Equal(TimeSpan.FromSeconds(seconds), ClientMetadataFetcher.Lifetime(cacheControl));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("192.0.0.8", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("2001::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void AddressTable_SeparatesPublicFromSpecialUse(string address, bool isPublic) =>
        Assert.Equal(isPublic, ClientMetadataFetcher.IsPublic(IPAddress.Parse(address)));
}
