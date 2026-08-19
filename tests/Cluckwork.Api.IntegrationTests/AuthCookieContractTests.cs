namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

// #145 — the refresh token is delivered only as an HttpOnly cookie; the body
// carries just the access token. CSRF posture: refresh/logout require the custom
// header. These assert the wire contract.
[Collection(IntegrationCollection.Name)]
public sealed class AuthCookieContractTests(CluckworkWebApplicationFactory factory)
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private async Task<(HttpResponseMessage Response, string SetCookie)> LoginRawAsync()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var response = await factory.CreateClient(Cookieless).PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v)
            ? v.First(c => c.StartsWith(AuthCookies.RefreshCookieName + "=", StringComparison.Ordinal))
            : "";
        return (response, setCookie);
    }

    [Fact]
    public async Task Login_sets_httponly_strict_pathscoped_cookie_and_omits_refresh_from_body()
    {
        var (response, setCookie) = await LoginRawAsync();

        // Cookie attributes (case-insensitive per the Set-Cookie grammar).
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);

        // Body must carry the access token but NOT the refresh token.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _),
            "the refresh token must never appear in the response body");
    }

    [Fact]
    public async Task Refresh_without_the_csrf_header_is_forbidden()
    {
        var tokens = await factory.LoginAsync(
            await SeedAsync());
        var response = await factory.CreateClient(Cookieless)
            .PostRefreshAsync(tokens.RefreshToken, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_unauthorized()
    {
        var response = await factory.CreateClient(Cookieless).PostRefreshAsync(refreshToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #309 — the MaxRefreshTokenLength guard in AuthEndpoints.Refresh had zero
    // test coverage before this (verified by grep). This locks in Fix 5b's
    // corrected understanding: an over-length cookie is treated like a MISSING
    // cookie (clear + the same "Not authenticated." 401 as the test above), not
    // like a genuine-but-rejected token from RefreshAsync (which returns
    // result.Error.Description, a different string).
    [Fact]
    public async Task Refresh_with_an_over_length_cookie_is_401_and_clears_the_cookie()
    {
        var overLength = new string('a', 600); // > MaxRefreshTokenLength (512)
        var response = await factory.CreateClient(Cookieless).PostRefreshAsync(overLength);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies),
            "an over-length refresh cookie must still be cleared with a Set-Cookie response");
        var refreshCookie = cookies!.First(
            c => c.StartsWith(AuthCookies.RefreshCookieName + "=", StringComparison.Ordinal));
        // Response.Cookies.Delete emits an expired (Unix-epoch) Expires
        // attribute, so the browser drops the 600-char garbage value instead of
        // continuing to store/present it — this is what "cleared" looks like on
        // the wire.
        Assert.Contains("expires=Thu, 01 Jan 1970", refreshCookie, StringComparison.OrdinalIgnoreCase);
    }

    // #309 Fix 3 — refresh binds no JSON body parameter (the token rides the
    // cookie), so nothing reads Request.Body unless the handler actively drains
    // it; without that, the middleware's byte-capped stream never fires and an
    // oversized/chunked body sails through untouched (reproduced live under
    // both TestServer and real Kestrel). Simulate an undeclared-length body the
    // same way AuthBodyLimitTests does under TestServer: declare a
    // Content-Length UNDER the 1 KB refresh cap while the stream actually holds
    // far more bytes, so the declared-length short-circuit (layer 2) passes and
    // only an ACTIVE READ of the body (the drain this fix adds) can catch it.
    // No CSRF header or cookie is presented — the drain runs first (cheapest
    // rejection first) and must 413 before either of those checks are reached.
    [Fact]
    public async Task Refresh_actively_drains_an_oversized_body_and_413s_before_the_csrf_check()
    {
        var bytes = new byte[8192];
        var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = 10; // lying: well under the 1 KB refresh cap
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = content };

        var response = await factory.CreateClient(Cookieless).SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    // Mirrors AuthBodyLimitTests.NonSeekableStream: a stream that can't report
    // its own Length, paired with a lying Content-Length header, is what forces
    // TestServer's ClientHandler to report an under-cap declared length while
    // still yielding the full byte count on read.
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Logout_without_the_csrf_header_is_forbidden()
    {
        var email = await SeedAsync();
        var tokens = await factory.LoginAsync(email);
        var response = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<string> SeedAsync()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return email;
    }
}

// The Secure attribute only appears when the request is HTTPS; drive login
// through a trusted proxy presenting X-Forwarded-Proto: https (reusing the #144
// forwarded-headers setup) to observe it.
public sealed class AuthCookieSecureTests(SecurityProxyFactory factory)
    : IClassFixture<SecurityProxyFactory>
{
    [Fact]
    public async Task Refresh_cookie_is_marked_secure_over_forwarded_https()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Remote", SecurityProxyFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        response.EnsureSuccessStatusCode();

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthCookies.RefreshCookieName + "=", StringComparison.Ordinal));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
