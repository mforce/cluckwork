namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Hosting;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #141 — static-asset cache policy. The suite normally runs with no wwwroot (the
// built SPA only exists in the Docker image), so this factory writes a throwaway
// web root — a hashed asset under /assets, an index.html, a root favicon — and
// points the host at it, so the real static + SPA-fallback pipeline serves them.
public sealed class StaticCachingFactory : CluckworkWebApplicationFactory
{
    private readonly string _webRoot = Path.Combine(
        Path.GetTempPath(), "cluckwork-static-" + Guid.NewGuid().ToString("N"));

    public const string HashedAsset = "/assets/app-deadbeef.js";

    public StaticCachingFactory()
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"),
            "<!doctype html><title>cluckwork</title>");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app-deadbeef.js"),
            "console.log('hashed bundle');");
        File.WriteAllText(Path.Combine(_webRoot, "favicon.ico"), "icon-bytes");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseWebRoot(_webRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            try { Directory.Delete(_webRoot, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class StaticAssetCachingTests(StaticCachingFactory factory)
    : IClassFixture<StaticCachingFactory>
{
    private static string? CacheControl(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Cache-Control", out var v) ? string.Join(", ", v) : null;

    [Fact]
    public async Task Hashed_asset_is_immutable_for_a_year()
    {
        var res = await factory.CreateClient().GetAsync(StaticCachingFactory.HashedAsset);

        res.EnsureSuccessStatusCode();
        Assert.Equal(StaticAssetCaching.ImmutableAsset, CacheControl(res));
    }

    [Fact]
    public async Task Index_html_requested_directly_always_revalidates()
    {
        var res = await factory.CreateClient().GetAsync("/index.html");

        res.EnsureSuccessStatusCode();
        Assert.Equal(StaticAssetCaching.AlwaysRevalidate, CacheControl(res));
    }

    [Fact]
    public async Task Spa_fallback_route_serves_index_html_and_always_revalidates()
    {
        // An unknown, non-API route: MapFallbackToFile rewrites it to index.html.
        // It must carry the SAME no-cache header as a direct index.html request,
        // or a fronting CDN could pin an old app after a deploy.
        var res = await factory.CreateClient().GetAsync("/some/deep/spa-route");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("cluckwork", await res.Content.ReadAsStringAsync());
        Assert.Equal(StaticAssetCaching.AlwaysRevalidate, CacheControl(res));
    }

    [Fact]
    public async Task Root_static_file_revalidates_not_immutable()
    {
        // Non-hashed root files (favicon, manifest, …) are safe but unversioned,
        // so they revalidate rather than being pinned immutable-forever.
        var res = await factory.CreateClient().GetAsync("/favicon.ico");

        res.EnsureSuccessStatusCode();
        Assert.Equal(StaticAssetCaching.AlwaysRevalidate, CacheControl(res));
    }

    [Fact]
    public async Task Api_response_is_unaffected_by_the_static_cache_policy()
    {
        // A real API route (protected → 401 unauthenticated). Asserting 401 also
        // guards the test: if this ever fell through to the SPA fallback it would
        // be a 200 with no-cache, and this would fail loudly rather than pass.
        var res = await factory.CreateClient().GetAsync("/api/v1/flocks");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Cache-Control"),
            "API responses must not carry the static cache header");
    }

    [Theory]
    [InlineData("/assets/missing-route")] // extensionless miss under /assets
    [InlineData("/assets")]               // exactly /assets, no file there
    public async Task Missing_asset_path_falls_back_to_index_and_is_never_immutable(string path)
    {
        // A miss under /assets is served by the SPA fallback as index.html. It
        // MUST get no-cache, never `immutable` — otherwise a CDN would pin the
        // app HTML for a year at that URL. (This locks the exact regression two
        // reviewers predicted; the framework rewrites the path to /index.html,
        // and AlwaysRevalidateHeader makes it correct regardless.)
        var res = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StaticAssetCaching.AlwaysRevalidate, CacheControl(res));
    }

    [Fact]
    public async Task Missing_asset_with_a_file_extension_is_a_plain_404_no_cache_header()
    {
        // A dotted miss (looks like a file) is excluded from the SPA fallback's
        // non-file constraint, so it 404s from the static pipeline with no header.
        var res = await factory.CreateClient().GetAsync("/assets/missing.js");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(res.Headers.Contains("Cache-Control"));
    }

    [Fact]
    public async Task Unknown_api_path_404s_as_an_api_error_not_the_cached_spa()
    {
        // An unmatched /api/* route must NOT fall through to the SPA fallback
        // (index.html + no-cache) — it must be a clean API 404 with no cache
        // header, so the '/api is unaffected' guarantee actually holds. (Regressed
        // silently before the /api catch-all: unknown /api returned 200 html.)
        var res = await factory.CreateClient().GetAsync("/api/v1/not-a-real-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.NotEqual("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.False(res.Headers.Contains("Cache-Control"),
            "an unknown API path must not carry the static cache header");
    }
}
