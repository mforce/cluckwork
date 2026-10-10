using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using Cluckwork.Domain.Common;

namespace Cluckwork.Infrastructure.OAuth;

internal readonly record struct FetchedDocument(byte[] Json, TimeSpan Lifetime);

// The fetcher's only test seam. Tests keep the address policy and every handler setting,
// and replace only how a host name resolves, where a vetted address is dialled and which
// certificate is trusted. No configuration reaches it.
internal sealed record FetchTransport(
    Func<string, CancellationToken, Task<IPAddress[]>> Resolve,
    Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Dial,
    RemoteCertificateValidationCallback? CertificateValidation)
{
    public static FetchTransport System { get; } = new(Dns.GetHostAddressesAsync, DialAsync, null);

    private static async ValueTask<Stream> DialAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

// #1148 — fetches a client ID metadata document from a URL the client chose, so it is a
// server-side request forgery surface. It resolves the host once, refuses the host if
// any address it resolves to is special-use (RFC 6890), and connects to the address it
// checked, so nothing resolves the name again for DNS rebinding to change. It follows no
// redirect, sends no cookie, credential or proxy request, and stops at a deadline and a
// size cap that also hold while the body streams.
internal sealed class ClientMetadataFetcher(
    FetchTransport transport, IReadOnlySet<string> privateHosts, TimeSpan? deadline = null) : IDisposable
{
    // The draft's recommended maximum (§8.7).
    public const int MaxBodyBytes = 5 * 1024;
    public const string UserAgent = "Cluckwork-OAuth-ClientMetadata/1.0";

    // A document is re-fetched after its Cache-Control lifetime, held between these bounds.
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Default = TimeSpan.FromHours(1);
    public static readonly TimeSpan Ceiling = TimeSpan.FromHours(24);

    // One deadline covers resolving, connecting, the headers and the whole body.
    private readonly TimeSpan _deadline = deadline ?? TimeSpan.FromSeconds(5);

    public const string AddressRefused = "metadata.address_refused";
    public const string Unreachable = "metadata.unreachable";
    public const string TimedOut = "metadata.timeout";
    public const string Redirected = "metadata.redirected";
    public const string BadStatus = "metadata.status";
    public const string BadContentType = "metadata.content_type";
    public const string TooLarge = "metadata.too_large";
    private static readonly string TooLargeDescription = $"The metadata document is larger than {MaxBodyBytes} bytes.";

    // IANA's IPv4 and IPv6 Special-Purpose Address Registries (RFC 6890), checked
    // 2026-10-10, plus IPv4 multicast; narrower registry entries sit inside these blocks.
    // IPv6 is an allow-list: 2000::/3 minus the registry blocks inside it, so every other
    // registry entry (loopback, NAT64, unique-local, link-local, ...) never qualifies.
    private static readonly IPNetwork[] RefusedV4 =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24", "192.168.0.0/16",
            "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        }.Select(IPNetwork.Parse),
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    private static readonly IPNetwork[] RefusedV6 =
        [.. new[] { "2001::/23", "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3fff::/20" }.Select(IPNetwork.Parse)];

    // What OAuth:ClientMetadata:PrivateHosts may reach: private networks, never loopback,
    // link-local (cloud metadata services live there) or multicast.
    private static readonly IPNetwork[] PrivateNetworks =
        [.. new[] { "10.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7" }.Select(IPNetwork.Parse)];

    private readonly HttpClient _http = new(CreateHandler(transport, privateHosts))
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders =
        {
            Accept = { new MediaTypeWithQualityHeaderValue("application/json") },
            UserAgent = { ProductInfoHeaderValue.Parse(UserAgent) },
        },
    };

    public async Task<Result<FetchedDocument>> FetchAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_deadline);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                return Fail(Redirected, "The metadata document URL redirects, and redirects are not followed.");
            if (response.StatusCode != HttpStatusCode.OK)
                return Fail(BadStatus, "The metadata document URL did not answer 200 OK.");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Fail(BadContentType, "The metadata document is not served as application/json.");
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return Fail(TooLarge, TooLargeDescription);

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxBodyBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
                length += read;
            if (length > MaxBodyBytes)
                return Fail(TooLarge, TooLargeDescription);

            return new FetchedDocument(buffer[..length], Lifetime(response.Headers.CacheControl));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(TimedOut, "The metadata document did not arrive in time.");
        }
        catch (HttpRequestException exception) when (exception.InnerException is AddressRefusedException)
        {
            return Fail(AddressRefused, "The metadata document's host resolves to an address this server does not fetch from.");
        }
        // HttpRequestException before the headers; IOException (HttpIOException among
        // them) when the connection fails while the body is read.
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Fail(Unreachable, "The metadata document could not be fetched.");
        }
    }

    // A document asking not to be stored still gets the floor: the application row that
    // anchors its approvals is the cache, and the floor bounds how often it is fetched.
    internal static TimeSpan Lifetime(CacheControlHeaderValue? cacheControl) =>
        cacheControl is { NoStore: true } or { NoCache: true }
            ? Floor
            : TimeSpan.FromTicks(Math.Clamp((cacheControl?.MaxAge ?? Default).Ticks, Floor.Ticks, Ceiling.Ticks));

    // An IPv4-mapped IPv6 address is refused before any range is consulted: .NET 10's
    // IPNetwork.Contains places ::ffff:10.0.0.1 inside 2000::/3.
    internal static bool IsPublic(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => !RefusedV4.Any(network => network.Contains(address)),
        AddressFamily.InterNetworkV6 => !address.IsIPv4MappedToIPv6 && GlobalUnicastV6.Contains(address)
            && !RefusedV6.Any(network => network.Contains(address)),
        _ => false,
    };

    internal static SocketsHttpHandler CreateHandler(FetchTransport transport, IReadOnlySet<string> privateHosts) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxResponseHeadersLength = 8,
        SslOptions = { RemoteCertificateValidationCallback = transport.CertificateValidation },
        ConnectCallback = async (context, ct) =>
        {
            // Resolved here and nowhere else. A proxy is off because it would resolve the
            // name itself, after this check.
            var host = context.DnsEndPoint.Host;
            var addresses = await transport.Resolve(host, ct);
            if (addresses.Length == 0 || !addresses.All(address => IsPublic(address)
                    || privateHosts.Contains(host) && !address.IsIPv4MappedToIPv6
                        && PrivateNetworks.Any(network => network.Contains(address))))
                throw new AddressRefusedException();
            return await transport.Dial(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct);
        },
    };

    private static Result<FetchedDocument> Fail(string code, string description) =>
        Result.Failure<FetchedDocument>(new Error(code, description));

    public void Dispose() => _http.Dispose();

    private sealed class AddressRefusedException : IOException;
}
