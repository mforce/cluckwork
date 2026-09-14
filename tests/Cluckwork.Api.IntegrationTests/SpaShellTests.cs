namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.RegularExpressions;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.Security;

// #873 — the SPA's index.html is served as a templated response so the document
// and its own Content-Security-Policy header name the SAME style nonce. If they
// ever disagree, MUI's Emotion styles are dropped by the browser and nothing
// says so: the page renders, the console carries one CSP line, and every test
// that reads the DOM rather than the rendered result still passes.
//
// Reuses StaticCachingFactory (#141), which already writes a throwaway web root:
// the suite otherwise runs with no wwwroot, because the built SPA only exists in
// the Docker image.
public sealed class SpaShellTests(StaticCachingFactory factory)
    : IClassFixture<StaticCachingFactory>
{
    private static readonly Regex NonceMetaPattern =
        new(@"<meta name=""csp-nonce"" content=""(?<nonce>[^""]*)"">", RegexOptions.Compiled);

    private static string MetaNonce(string html)
    {
        var match = NonceMetaPattern.Match(html);
        Assert.True(match.Success, "the served document carries no csp-nonce meta");
        return match.Groups["nonce"].Value;
    }

    private static string? CacheControl(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Cache-Control", out var v) ? string.Join(", ", v) : null;

    [Theory]
    [InlineData("/")]                 // what a person opens
    [InlineData("/index.html")]       // what workbox fetches to precache (#142)
    [InlineData("/some/spa/route")]   // client-side routing, via the fallback
    public async Task Served_document_carries_the_same_nonce_as_its_own_header(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);

        var header = SecurityHeadersTests.StyleNonce(
            res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(header, MetaNonce(await res.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Two_requests_get_two_different_nonces()
    {
        var client = factory.CreateClient();

        var first = MetaNonce(await client.GetStringAsync("/"));
        var second = MetaNonce(await client.GetStringAsync("/"));

        // The failure this catches is the cheap implementation: mint once at
        // boot, hand the same value to every client forever. The page would look
        // right, the header would match, and the nonce would be worthless.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task The_document_is_otherwise_the_built_file()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        // Templating is an INSERTION, not a rewrite: everything the build emitted
        // is still there, and the meta sits inside <head> where a parser will
        // read it before the bundle runs.
        Assert.Equal(
            StaticCachingFactory.IndexHtml,
            NonceMetaPattern.Replace(html, string.Empty));
        Assert.Contains($"<head><meta name=\"{SecurityHeaders.NonceMetaName}\"", html);
    }

    [Fact]
    public async Task The_shell_revalidates_and_carries_no_stale_validator()
    {
        var res = await factory.CreateClient().GetAsync("/");

        // #141's policy survives: revalidate, never immutable.
        Assert.Equal(StaticAssetCaching.AlwaysRevalidate, CacheControl(res));
        Assert.NotEqual(StaticAssetCaching.ImmutableAsset, CacheControl(res));

        // And no validator, which is new (#873). The static-file middleware used
        // to emit an ETag and Last-Modified derived from the FILE — both now lie,
        // because each response's body differs. A client honouring a 304 would
        // keep a document whose nonce the live header no longer admits, and MUI
        // would lose its styling on exactly the best-behaved caches.
        Assert.Null(res.Headers.ETag);
        Assert.Null(res.Content.Headers.LastModified);
    }

    [Theory]
    [InlineData("/api/v1/not-a-real-endpoint")]
    [InlineData("/health/not-a-real-check")]
    public async Task Api_and_health_guards_still_outrank_the_shell(string path)
    {
        // #266 — an unknown /health/* answered with a 200 text/html shell would
        // make the container HEALTHCHECK report a dead app healthy, and an
        // unknown /api/* answered the same way would put a cache header on an
        // API response (#141). The shell is the lowest-priority route and must
        // stay that way; these two are the reason it is not a bare catch-all.
        var res = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Hashed_assets_are_untouched_and_still_immutable()
    {
        var res = await factory.CreateClient().GetAsync(StaticCachingFactory.HashedAsset);

        res.EnsureSuccessStatusCode();
        Assert.Equal(StaticAssetCaching.ImmutableAsset, CacheControl(res));
        Assert.DoesNotContain("csp-nonce", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_document_with_no_head_is_refused_at_load()
    {
        // The boot-time half of the fail-closed posture. An index.html that
        // cannot be templated would otherwise start, pass /health/ready and
        // serve an app with no MUI styling — the #510 shape, where a check
        // deferred past boot becomes an invisible per-request defect.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => SpaShell.FromDocument("<!doctype html><body>no head here</body>"));

        Assert.Contains("<head>", thrown.Message, StringComparison.Ordinal);
    }
}
