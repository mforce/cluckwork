namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;

// #342 — follow-up to #340/#341. IHttpMaxRequestBodySizeFeature (the transport-
// level cutoff RequestBodyLimit.cs, FarmLogoEndpoints and ClientErrorEndpoints
// all set best-effort) is ABSENT under the in-memory TestServer, so the rest of
// the suite proves layers (2)/(3) — the declared-Content-Length short-circuit
// and the portable read-cap — using a LYING Content-Length, never a genuinely
// undeclared-length (chunked) body actually cut off by Kestrel itself. These
// tests exercise that transport layer for real, over the same UseKestrel(0)
// factory #341 introduced.
//
// Only the login test also asserts the completion log stayed clean, per
// RequestBodyLimit.cs:85's own worry (the #340 class of bug: a response that
// commits successfully while masking a throw behind it) — that guarantee is
// specific to WithMaxRequestBodyBytes' swallow-and-recover design. The logo
// and client-error tests measured, empirically, that which of the endpoint's
// own read-loop cap vs. Kestrel's transport cutoff notices an oversized body
// first is a genuine race that resolves differently at different cap sizes —
// sometimes reaching /error as a real unhandled BadHttpRequestException, which
// Program.cs documents as an intentional Error-level log, not a #340-shaped
// bug. Those two tests assert only the client-visible 413 contract, which
// holds regardless of which side of the race wins.
public sealed class KestrelRequestBodyLimitTests(KestrelBackedFactory factory)
    : IClassFixture<KestrelBackedFactory>
{
    private const string LoginPath = "/api/v1/auth/login";
    private const string LogoPath = "/api/v1/account/logo";
    private const string ClientErrorPath = "/api/v1/client-errors";

    private const int LoginCapBytes = 4096;
    // Production defaults to 2 MB (FarmLogoOptions.MaxUploadBytes); the test
    // host pins it lower via CluckworkWebApplicationFactory.LogoUploadCap.
    private const int LogoCapBytes = CluckworkWebApplicationFactory.LogoUploadCap;
    private const int ClientErrorCapBytes = 16 * 1024;

    // A 1x1 RGBA PNG as an encoder produces it — same fixture as FarmLogoTests,
    // reproduced here rather than shared because it's a four-line constant, not
    // logic (contrast KestrelLogAssertions, extracted because that WAS logic).
    private static readonly byte[] TinyPng = Convert.FromHexString(
        "89504E470D0A1A0A" +
        "0000000D49484452000000010000000108060000001F15C489" +
        "0000000A49444154789C63000100000500010D0A2DB4" +
        "0000000049454E44AE426082");

    private sealed record ProblemFields(string? Type, string? Title, string? Detail, int? Status);

    private static async Task<ProblemFields> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new ProblemFields(Str("type"), Str("title"), Str("detail"), Int("status"));
    }

    // A genuinely chunked request: StreamContent over a non-seekable stream,
    // with Transfer-Encoding: chunked forced explicitly. That force is
    // load-bearing, not decorative — measured empirically (a diagnostic log of
    // Request.ContentLength/TransferEncoding on the server side): StreamContent
    // over a CanSeek=false stream does NOT, on its own, make SocketsHttpHandler
    // negotiate chunked encoding; the handler still computed and sent a real
    // Content-Length matching the body's true size. Without this, these tests
    // would silently exercise the declared-length path (2) — already covered by
    // AuthBodyLimitTests' lying-Content-Length trick — instead of the
    // undeclared-length transport path (1)/(3) #342 is actually about.
    private static HttpRequestMessage ChunkedRequest(
        HttpMethod method, string path, byte[] body, string contentType)
    {
        var content = new StreamContent(new NonSeekableStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TransferEncodingChunked = true;
        return request;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var email = $"kestrel-body-cap-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    // --- 1. Login — the RequestBodyLimit.WithMaxRequestBodyBytes middleware ---

    [Fact]
    public async Task Chunked_oversized_login_body_is_413_over_kestrel_and_completes_cleanly()
    {
        var client = factory.CreateClient();
        var oversized = Encoding.UTF8.GetBytes(
            $$"""{"email":"nobody@example.com","password":"{{new string('a', LoginCapBytes * 2)}}"}""");
        var mark = factory.MarkLog();

        var response = await client.SendAsync(ChunkedRequest(HttpMethod.Post, LoginPath, oversized, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        // The canonical shape RequestBodyLimit.WriteBodyTooLargeAsync always
        // writes, regardless of whether Kestrel's own transport cutoff or our
        // portable read cap is what actually threw — see that method's comment
        // for why both origins converge here.
        Assert.Equal("Invalid request body", problem.Title);
        Assert.Equal($"The request body exceeds the {LoginCapBytes}-byte limit for this endpoint.", problem.Detail);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        await factory.AssertServerSideCleanAsync(LoginPath, mark, expected: 1);
    }

    [Fact]
    public async Task Login_body_just_under_the_cap_is_not_413_over_kestrel()
    {
        var client = factory.CreateClient();
        // Well under the 4 KB cap: not accepted (wrong credentials), but that
        // must be a 401, never a 413 — proves the transport cutoff isn't
        // capping every request regardless of size.
        var response = await client.PostAsJsonAsync(
            LoginPath, new { email = "nobody@example.com", password = new string('a', 200) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- 2. Farm logo upload — FarmLogoEndpoints' own hand-rolled cap (#123) ---

    [Fact]
    public async Task Chunked_oversized_logo_upload_is_413_over_kestrel()
    {
        var client = await AdminClientAsync();
        var oversized = new byte[LogoCapBytes + 4096];
        Array.Fill(oversized, (byte)0xAA);

        var request = ChunkedRequest(HttpMethod.Put, LogoPath, oversized, "image/png");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        // Which of FarmLogoEndpoints' own bounded read loop vs. Kestrel's
        // IHttpMaxRequestBodySizeFeature notices the overflow first is a real
        // race, not a fixed property of this code — see the client-error test,
        // where at a smaller cap the other side wins and the response comes
        // from an unhandled exception reaching /error instead. This test only
        // pins down what's actually guaranteed at this (64 KB test) cap size:
        // FarmLogoEndpoints' own loop wins, so its own ProblemDetails shape is
        // what the client gets.
        Assert.Equal("FarmLogo.TooLarge", problem.Title);
        Assert.Equal($"The logo must be {LogoCapBytes / 1024} KB or smaller.", problem.Detail);
    }

    [Fact]
    public async Task Logo_upload_under_the_cap_still_succeeds_over_kestrel()
    {
        var client = await AdminClientAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, LogoPath) { Content = new ByteArrayContent(TinyPng) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- 3. Client-error report — ClientErrorEndpoints' own hand-rolled cap (#217) ---

    [Fact]
    public async Task Chunked_oversized_client_error_report_is_413_over_kestrel()
    {
        var client = factory.CreateClient();
        var oversized = Encoding.UTF8.GetBytes(
            $$"""{"scope":"app","message":"{{new string('a', ClientErrorCapBytes * 2)}}"}""");

        var response = await client.SendAsync(
            ChunkedRequest(HttpMethod.Post, ClientErrorPath, oversized, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        // Measured, not the same result as the logo case despite the identical
        // hand-rolled-loop shape: at this (smaller, 16 KB) cap, Kestrel's own
        // IHttpMaxRequestBodySizeFeature DOES abort the read before
        // ClientErrorEndpoints' own loop notices the overflow, so this actually
        // IS an unhandled BadHttpRequestException reaching /error's generic
        // mapping — not this endpoint's own "Report too large" ProblemDetails.
        // Which of the two fires is apparently a real timing race, not a fixed
        // property of the code — see the logo test for the other outcome of the
        // same race at a different cap size.
        Assert.Equal("Invalid request body", problem.Title);
        // No AssertServerSideCleanAsync here, unlike the login test: this IS a
        // genuine unhandled BadHttpRequestException reaching /error, and
        // Program.cs's UseSerilogRequestLogging.GetLevel documents that a
        // non-400 BadHttpRequestException "correctly stays Error" — by design,
        // not the #340 class of bug (a response that silently masks a throw
        // behind an already-committed success status). The client-visible
        // contract (413 + this ProblemDetails shape) is what's under test here.
    }

    [Fact]
    public async Task Client_error_report_under_the_cap_still_succeeds_over_kestrel()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            ClientErrorPath, new { scope = "app", message = "a render crash, well under the cap" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
