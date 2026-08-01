namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #312 — origin-side safe cache default. The app previously emitted no
// Cache-Control on API responses, so a browser cache, a misconfigured
// intermediary, or a future edge rule could retain tenant data. These tests
// assert the policy lands on every authenticated/tenant-data-bearing response
// class (read, write, auth, validation error, export) while confirming the two
// carve-outs — health probes and any deliberately-set header (static/SPA
// assets, the farm logo's own revalidate policy) — are untouched.
//
// Assertions read the strongly-typed CacheControlHeaderValue rather than
// comparing raw strings: .NET's HttpResponseMessage re-serializes Cache-Control
// into a fixed directive order when the header is read back (confirmed via a
// throwaway repro: parsing "private, no-store" round-trips as "no-store,
// private"), so a literal string comparison here would be testing .NET's
// canonicalization, not this app's wire format.
[Collection(IntegrationCollection.Name)]
public sealed class ResponseCacheControlTests(CluckworkWebApplicationFactory factory)
{
    private static CacheControlHeaderValue? CacheControl(HttpResponseMessage res) => res.Headers.CacheControl;

    private static void AssertPrivateNoStore(HttpResponseMessage res)
    {
        var cc = CacheControl(res);
        Assert.NotNull(cc);
        Assert.True(cc!.NoStore, "expected the no-store directive");
        Assert.True(cc.Private, "expected the private directive");
    }

    private async Task<(HttpClient Client, string Email)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, email);
    }

    [Fact]
    public async Task Authenticated_read_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Authenticated_write_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Successful_login_response_is_no_store()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var res = await factory.TryLoginAsync(email, TestHarness.Password);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Failed_login_response_is_no_store_even_though_the_caller_is_not_authenticated()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var res = await factory.TryLoginAsync(email, "definitely-the-wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Anonymous_request_to_a_protected_route_gets_a_no_store_401()
    {
        var res = await factory.CreateClient().GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Validation_error_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "No Phone" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Unknown_api_path_404_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.GetAsync("/api/v1/not-a-real-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Csv_export_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.GetAsync("/api/v1/export/customers");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Full_backup_export_response_is_no_store()
    {
        var (client, _) = await SetupAsync();

        var res = await client.GetAsync("/api/v1/export/all");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        AssertPrivateNoStore(res);
    }

    [Fact]
    public async Task Health_live_keeps_its_own_no_caching_headers_not_the_default_policy()
    {
        // The built-in health-check middleware already sets its own
        // Cache-Control/Expires/Pragma to prevent caching (AllowCachingResponses
        // defaults to false) — confirmed via a throwaway probe: "no-store,
        // no-cache", no `private`. /health is excluded from #312's default so
        // that pre-existing, framework-owned contract is never masked/overridden
        // by this app's blanket policy (mirrors the existing /health carve-outs
        // in Program.cs's Serilog level selector and the /health/{**rest}
        // catch-all).
        var res = await factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cc = CacheControl(res);
        Assert.NotNull(cc);
        Assert.True(cc!.NoCache);
        Assert.True(cc.NoStore);
        Assert.False(cc.Private, "the health middleware's own header has no `private` directive");
    }

    [Fact]
    public async Task Unknown_health_path_gets_no_cache_control_header_at_all()
    {
        // The /health/{**rest} catch-all (#266) returns a plain ProblemDetails
        // 404 with no framework-owned Cache-Control of its own — unlike
        // /health/live above, nothing here would mask a missing exclusion, so
        // this is the case that actually proves the /health carve-out fires:
        // without it, this response would gain the default private/no-store.
        var res = await factory.CreateClient().GetAsync("/health/not-a-real-check");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(CacheControl(res));
        Assert.False(res.Headers.Contains("Cache-Control"));
    }
}
