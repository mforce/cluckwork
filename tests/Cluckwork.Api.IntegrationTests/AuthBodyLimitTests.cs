namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #309 — an oversized credential must be rejected BEFORE JSON binding and BEFORE
// the PBKDF2 hasher (including the unknown-user timing-equalization hash), while
// within-limit inputs keep the existing non-enumerating 401 and still pay the
// verify. The instrumented hasher below proves which side of the cutoff a request
// lands on: verify-count 0 = never hashed.
public sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new();
    private int _hashCount;
    private int _verifyCount;

    public int HashCount => Volatile.Read(ref _hashCount);
    public int VerifyCount => Volatile.Read(ref _verifyCount);

    public void Reset()
    {
        Interlocked.Exchange(ref _hashCount, 0);
        Interlocked.Exchange(ref _verifyCount, 0);
    }

    public string HashPassword(ApplicationUser user, string password)
    {
        Interlocked.Increment(ref _hashCount);
        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verifyCount);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

// Own factory (not the shared collection) so the counting hasher is a private
// singleton these sequential tests can reset and assert against.
public sealed class AuthBodyLimitFactory : CluckworkWebApplicationFactory
{
    public CountingPasswordHasher Hasher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Registered after the app's Identity default, so UserManager resolves THIS.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(Hasher));
    }
}

public sealed class AuthBodyLimitTests(AuthBodyLimitFactory factory)
    : IClassFixture<AuthBodyLimitFactory>
{
    private const string LoginPath = "/api/v1/auth/login";

    [Fact]
    public async Task Oversized_login_body_is_413_and_never_hashed()
    {
        factory.Hasher.Reset();
        var client = factory.CreateClient();

        // ~8 KB password → body well over the 4 KB login cap; a declared
        // Content-Length is refused before binding or the hasher.
        var response = await client.PostAsJsonAsync(LoginPath,
            new { email = "nobody@example.com", password = new string('a', 8192) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.Hasher.VerifyCount);
    }

    [Fact]
    public async Task Chunked_oversized_login_body_is_413_and_never_hashed()
    {
        factory.Hasher.Reset();
        var client = factory.CreateClient();

        // The byte-cap READ guarantee (layer 3) — not the declared-length check
        // (layer 2) — must cut this off before the hasher. A merely non-seekable
        // stream doesn't exercise layer 3 under TestServer: TestHost's in-process
        // ClientHandler reads the whole StreamContent to relay it and reports the
        // REAL byte count as Request.ContentLength regardless of what the client
        // declared, so layer 2 alone would already catch a plain oversized body.
        // Declaring a Content-Length UNDER the cap while the stream actually
        // yields far more bytes (a "lying" length — the same thing a chunked
        // request with no declared length simulates for this middleware, per its
        // own layer-2/3 comments) is what makes layer 2 pass and forces layer 3's
        // read cap to be the one that fires, mid JSON-binding.
        var request = LyingLengthRequest(new string('a', 8192), declaredLength: 10);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.Hasher.VerifyCount);
    }

    // #309 Fix 1 — a declared-oversize body is refused by the middleware itself
    // (writes a ProblemDetails body directly), while a body whose ACTUAL bytes
    // exceed the cap despite an under-cap declared/lying Content-Length is caught
    // mid-JSON-binding, DURING minimal-API's generated binding code — measured
    // (see the debug narrative in this fix's PR/task notes) to be swallowed
    // there into a bare 413 with Content-Length: 0 and no Content-Type, never
    // reaching /error. This test locks in that both origins now produce the
    // SAME response contract.
    [Fact]
    public async Task Declared_length_and_chunked_413_bodies_have_the_same_shape()
    {
        var client = factory.CreateClient();

        var declared = await client.PostAsJsonAsync(LoginPath,
            new { email = "nobody@example.com", password = new string('a', 8192) });

        var chunked = await client.SendAsync(
            LyingLengthRequest(new string('a', 8192), declaredLength: 10));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal("application/problem+json", declared.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/problem+json", chunked.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await ProblemFields(declared), await ProblemFields(chunked));
    }

    // Builds a login POST whose HttpContent declares `declaredLength` (under the
    // cap) via Content-Length while its underlying stream actually holds far
    // more bytes (the given password, JSON-escaped). See the comment on
    // Chunked_oversized_login_body_is_413_and_never_hashed for why this — not a
    // merely non-seekable stream — is what reaches this middleware's layer-3
    // read cap under TestServer.
    private static HttpRequestMessage LyingLengthRequest(string password, long declaredLength)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{{\"email\":\"nobody@example.com\",\"password\":\"{password}\"}}");
        var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = declaredLength;
        return new HttpRequestMessage(HttpMethod.Post, LoginPath) { Content = content };
    }

    [Fact]
    public async Task Within_cap_body_with_password_over_256_is_400_and_never_hashed()
    {
        factory.Hasher.Reset();
        var client = factory.CreateClient();

        // 257-char password: body is well under the 4 KB cap, so it binds — but
        // the login validator's max-length rule 400s it before the hasher.
        var response = await client.PostAsJsonAsync(LoginPath,
            new { email = "nobody@example.com", password = new string('a', 257) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Hasher.VerifyCount);
    }

    // #309 Fix 8b — the 256 boundary was tested only on the rejection side
    // (257 above); nothing at the HTTP level proved a password of EXACTLY 256
    // reaches the hasher rather than being incorrectly rejected as oversized —
    // neither by the (now 4096-byte) body cap nor by the validator's
    // MaximumLength(256) rule.
    [Fact]
    public async Task AtCap_password_login_is_401_not_400_and_still_pays_the_equalization_hash()
    {
        factory.Hasher.Reset();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(LoginPath,
            new { email = $"ghost-{Guid.NewGuid():N}@example.com", password = new string('a', 256) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(factory.Hasher.VerifyCount >= 1,
            "an exactly-256-char password must reach the equalization hash, not be rejected as oversized");
    }

    [Fact]
    public async Task Within_limit_unknown_user_is_401_and_still_pays_the_equalization_hash()
    {
        factory.Hasher.Reset();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(LoginPath,
            new { email = $"ghost-{Guid.NewGuid():N}@example.com", password = "WrongPassw0rd!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The unknown-user path must STILL hash, or timing leaks user existence.
        Assert.True(factory.Hasher.VerifyCount >= 1,
            "within-limit unknown-user login must still pay the equalization hash");
    }

    [Fact]
    public async Task Known_user_wrong_password_is_indistinguishable_from_unknown_user()
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient();

        var unknown = await client.PostAsJsonAsync(LoginPath,
            new { email = $"ghost-{Guid.NewGuid():N}@example.com", password = "WrongPassw0rd!" });
        var known = await client.PostAsJsonAsync(LoginPath,
            new { email, password = "WrongPassw0rd!" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, known.StatusCode);
        // Same problem shape — the reply reveals nothing about account existence.
        Assert.Equal(await ProblemFields(unknown), await ProblemFields(known));
    }

    [Fact]
    public async Task Valid_login_still_succeeds()
    {
        var email = $"valid-{Guid.NewGuid():N}@example.com";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(LoginPath,
            new { email, password = TestHarness.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_authenticated_and_owner_only_bodies_are_413()
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        await factory.SeedAccountWithUserAsync(email); // Owner (users group is Owner-only)
        var token = await factory.LoginForAccessTokenAsync(email);
        var client = factory.CreateAuthedClient(token);

        // create-user (Owner-only, 8 KB cap): authenticate — an anonymous request
        // would 401 at the auth gate, but an oversized body 413s regardless.
        var createUser = await client.PostAsJsonAsync("/api/v1/users",
            new { email = "x@example.com", password = new string('a', 16384), role = "Worker" });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, createUser.StatusCode);

        // set-password (Owner-only, 2 KB cap, unchanged)
        var setPassword = await client.PutAsJsonAsync(
            $"/api/v1/users/{Guid.NewGuid()}/password",
            new { newPassword = new string('a', 4096) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, setPassword.StatusCode);

        // change-password (authenticated, 4 KB cap)
        var changePassword = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword = new string('a', 8192) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, changePassword.StatusCode);
    }

    private static async Task<(string? Title, string? Detail, int? Status)> ProblemFields(
        HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return (Str("title"), Str("detail"), Int("status"));
    }

}
