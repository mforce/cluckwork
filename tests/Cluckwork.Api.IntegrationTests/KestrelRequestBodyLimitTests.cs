namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;

// #342 — follow-up to #340/#341. IHttpMaxRequestBodySizeFeature (the transport-
// level cutoff RequestBodyLimit.cs, FarmLogoEndpoints and ClientErrorEndpoints
// all set best-effort) is ABSENT under the in-memory TestServer, so the rest of
// the suite proves layers (2)/(3) — the declared-Content-Length short-circuit
// and the portable read-cap — using a LYING Content-Length, never a genuinely
// undeclared-length (chunked) body actually sent over a real connection. These
// tests send one for real, over the same UseKestrel(0) factory #341 introduced
// — the scenario the rest of the suite structurally cannot reach at all.
//
// What these tests do NOT claim (codex review, PR #440): that the response
// came specifically from Kestrel's own IHttpMaxRequestBodySizeFeature (layer
// (1)) rather than the portable ByteCappedRequestStream read cap (layer (3)).
// Measured directly: with (1) disabled, the login test's assertions still pass
// unchanged, because (3) unconditionally wraps and re-catches the same bytes
// (1) would have. That is not a hole in these tests — RequestBodyLimit.cs's
// own comments already document (1) as best-effort and (3) as "the portable
// guarantee, not this", so a client-visible test cannot and is not meant to
// isolate (1) from (3); AuthBodyLimitTests already proves the (1)-absent case
// works (that's what running under TestServer literally is). What's new and
// real here is exercising the previously-untested case where a genuinely
// undeclared-length body crosses an actual socket into Kestrel.
//
// Only the login test also asserts the completion log stayed clean, per
// RequestBodyLimit.cs:85's own worry (the #340 class of bug: a response that
// commits successfully while masking a throw behind it) — that guarantee is
// specific to WithMaxRequestBodyBytes' swallow-and-recover design.
//
// The client-error test measured, empirically, that which of the endpoint's
// own read-loop cap vs. Kestrel's transport cutoff notices an oversized body
// first is a genuine race, and asserts only the client-visible 413 contract
// (accepting either outcome's shape) rather than which side won.
//
// The logo test is NOT the same kind of race as the client-error one, despite
// the superficially similar hand-rolled-cap shape: FarmLogoEndpoints' PUT
// requires auth AND an Idempotency-Key, so IdempotencyMiddleware.
// ComputeRequestHashAsync — which calls EnableBuffering() and reads the body
// to hash it — runs before FarmLogoEndpoints' own handler. #442 (codex review,
// PR #440) found that, at the time, the cap was armed INSIDE that handler, so
// idempotency's read always ran first, unbounded, before the endpoint's own
// cap-lowering could ever take effect (Kestrel's feature was already
// IsReadOnly by the time the handler got to it) — an oversized upload was
// still eventually rejected, but only after being fully buffered in memory.
// FarmLogoRequestBodyCap (Hosting/) now arms the SAME cap in a middleware
// registered before IdempotencyMiddleware, so the wrapped body stream — not
// just the endpoint's own read loop — is what idempotency's hash-read runs
// into. This test's assertions (status, title, detail) are unchanged, because
// FarmLogoRequestBodyCap converts that earlier throw back into the exact same
// FarmLogo.TooLarge response — but deterministically for a different reason
// now: it fires before idempotency buffers the body, not after.
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

    // For an endpoint with NO WithMaxRequestBodyBytes swallow-and-recover AND
    // no idempotency buffering ahead of it (ClientErrorEndpoints — anonymous,
    // no Idempotency-Key): whether the endpoint's own bounded read loop or
    // Kestrel's own IHttpMaxRequestBodySizeFeature notices an oversized
    // chunked body first is a genuine transport-buffering/scheduling race, not
    // a fixed property of the code (codex review, PR #440 — confirmed by
    // measurement: it varies run to run, nothing pins it to stay one way).
    // Both shapes are a 413 with a non-empty detail; accept either rather than
    // pinning one. NOT used for the logo test — see that test's own comment
    // for why IdempotencyMiddleware makes it deterministic there, not a race.
    private static void AssertEitherBodyTooLargeShape(
        ProblemFields problem, string endpointTitle, string endpointDetail)
    {
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problem.Status);
        if (problem.Title == "Invalid request body")
        {
            // Kestrel's own transport cutoff won the race: an unhandled
            // BadHttpRequestException reached /error's generic mapping. The
            // exact message is framework-internal, so only non-empty is
            // asserted — see /error's `BadHttpRequestException bad` arm.
            Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        }
        else
        {
            // The endpoint's own read loop won the race: its own ProblemDetails.
            Assert.Equal(endpointTitle, problem.Title);
            Assert.Equal(endpointDetail, problem.Detail);
        }
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
        // Sent chunked (not PostAsJsonAsync's declared Content-Length), like
        // the oversized test above — codex review, PR #440: a declared-length
        // control can't prove the chunked transport shape is accepted on its
        // own merits. If Kestrel or an endpoint ever regressed to rejecting
        // every undeclared-length body outright, the oversized test above
        // would still pass (still expects 413) while this one would catch it.
        var underCap = Encoding.UTF8.GetBytes(
            $$"""{"email":"nobody@example.com","password":"{{new string('a', 200)}}"}""");

        var response = await client.SendAsync(ChunkedRequest(HttpMethod.Post, LoginPath, underCap, "application/json"));

        // Not accepted (wrong credentials), but that must be a 401, never a
        // 413 — proves the transport cutoff isn't capping every request
        // regardless of size.
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
        // Deterministic, not a race — see the class-header comment (#442):
        // FarmLogoRequestBodyCap wraps the body in the read-capped stream
        // BEFORE IdempotencyMiddleware ever touches it, so the cap fires
        // during idempotency's hash-read rather than after it, and the
        // response is converted back to this same FarmLogo.TooLarge shape.
        Assert.Equal("FarmLogo.TooLarge", problem.Title);
        Assert.Equal($"The logo must be {LogoCapBytes / 1024} KB or smaller.", problem.Detail);
    }

    // #442 review (codex) — this class's two tests above prove an oversized
    // logo upload eventually gets rejected with the right shape, but that's
    // identical whether the cap fires before or after IdempotencyMiddleware
    // buffers the body, so they can't prove the ORDERING. Proving that over
    // a real socket turned out to be the wrong tool: an earlier version of
    // this file tried a byte-counted assertion here, sized against
    // LogoCapBytes, but how many bytes a client manages to push before a
    // server-side rejection actually stalls it depends on Kestrel's own
    // internal pipe buffering (PauseWriterThreshold, ~1 MB by default) and
    // the OS's TCP receive buffer — neither controlled by this repo, both
    // comfortably able to absorb far more than a modest multiple of
    // LogoCapBytes before the client ever blocks, REGARDLESS of whether the
    // fix is in place. No fixed threshold is both tight enough to catch a
    // regression and loose enough to survive normal buffering variance
    // across CI runners. FarmLogoRequestBodyCapTests proves the ordering
    // instead, in-process with a plain in-memory stream (no networking, no
    // buffering variance) — see that file for the actual regression-catching
    // test the codex comment asked for.

    [Fact]
    public async Task Logo_upload_under_the_cap_still_succeeds_over_kestrel()
    {
        var client = await AdminClientAsync();
        // Chunked, not ByteArrayContent's declared Content-Length — see the
        // login under-cap test's comment for why a declared-length control
        // can't prove this.
        var request = ChunkedRequest(HttpMethod.Put, LogoPath, TinyPng, "image/png");
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
        AssertEitherBodyTooLargeShape(
            problem, "Report too large", $"Error reports are capped at {ClientErrorCapBytes} bytes.");
        // No AssertServerSideCleanAsync here, unlike the login test: when
        // Kestrel's own cutoff wins this race, the result is a genuine
        // unhandled BadHttpRequestException reaching /error, and Program.cs's
        // UseSerilogRequestLogging.GetLevel documents that a non-400
        // BadHttpRequestException "correctly stays Error" — by design, not the
        // #340 class of bug (a response that silently masks a throw behind an
        // already-committed success status). The client-visible contract (413
        // + one of the two documented ProblemDetails shapes) is what's under
        // test here, not which internal path produced it or the log level.
    }

    [Fact]
    public async Task Client_error_report_under_the_cap_still_succeeds_over_kestrel()
    {
        var client = factory.CreateClient();
        // Chunked, not PostAsJsonAsync's declared Content-Length — see the
        // login under-cap test's comment for why a declared-length control
        // can't prove this. Padded to just under ClientErrorCapBytes, not a
        // trivial ~60-byte message (codex review, PR #440): a tiny body can't
        // catch a regression that shrinks the cap to, say, 1 KB — the
        // oversized test would still pass (still expects 413 at 2x the
        // INTENDED cap) while every real report in between got silently
        // rejected. This is the actual threshold, not just "some size works".
        // 128-byte margin, not 1: measured that Kestrel's own transport cutoff
        // (armed to ClientErrorCapBytes) counts raw chunked-encoding wire
        // bytes — chunk-size line + CRLFs — not just the decoded JSON payload,
        // so a body sized to exactly cap-1 content bytes still tripped it.
        const string scaffold = """{"scope":"app","message":""}""";
        const int margin = 128;
        var padding = ClientErrorCapBytes - Encoding.UTF8.GetByteCount(scaffold) - margin;
        var underCap = Encoding.UTF8.GetBytes(
            $$"""{"scope":"app","message":"{{new string('a', padding)}}"}""");
        Assert.True(
            underCap.Length < ClientErrorCapBytes && underCap.Length > ClientErrorCapBytes - margin - 16,
            $"expected the padded body within {margin + 16} bytes of the {ClientErrorCapBytes}-byte cap, was {underCap.Length}");

        var response = await client.SendAsync(
            ChunkedRequest(HttpMethod.Post, ClientErrorPath, underCap, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
