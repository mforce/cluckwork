namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;
using Microsoft.EntityFrameworkCore;

// #179 — the farm banner over the wire. FarmLogoTests.cs already proves
// ImageSanitizer's format/security guarantees survive the round trip; these
// cover what's NEW here — the banner's own route, its own size/error-code
// namespace, and that it is genuinely independent of the logo sharing its row
// (FarmLogo.cs's "own table vs shared row" tradeoff).
[Collection(IntegrationCollection.Name)]
public sealed class FarmBannerTests(CluckworkWebApplicationFactory factory)
{
    private const string BannerPath = "/api/v1/account/banner";
    private const string LogoPath = "/api/v1/account/logo";
    private const string AccountPath = "/api/v1/account";

    private sealed record BannerDto(
        string ContentType, string ContentHash, int Width, int Height, int ByteLength, DateTimeOffset UpdatedAt);
    private sealed record AccountDto(Guid Id, string Name, string? LogoContentHash, string? BannerContentHash);

    private async Task<(HttpClient Client, Guid AccountId)> AdminAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), accountId);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string path, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Admin_UploadsABanner_AndEveryoneCanFetchIt()
    {
        var (client, _) = await AdminAsync();

        var upload = await PutAsync(client, BannerPath, TinyPng);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = (await upload.Content.ReadFromJsonAsync<BannerDto>())!;
        Assert.Equal("image/png", meta.ContentType);

        var fetched = await client.GetAsync(BannerPath);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(TinyPng, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NoBanner_Returns404()
    {
        var (client, _) = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BannerPath)).StatusCode);
    }

    [Fact]
    public async Task RemovingTheBanner_LeavesTheLogoInPlace()
    {
        var (client, _) = await AdminAsync();
        await PutAsync(client, LogoPath, TinyPng);
        await PutAsync(client, BannerPath, TinyPng);

        var remove = await DeleteAsync(client, BannerPath);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(BannerPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(LogoPath)).StatusCode);
    }

    [Fact]
    public async Task RemovingTheLogo_LeavesTheBannerInPlace()
    {
        var (client, _) = await AdminAsync();
        await PutAsync(client, LogoPath, TinyPng);
        await PutAsync(client, BannerPath, TinyPng);

        var remove = await DeleteAsync(client, LogoPath);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(BannerPath)).StatusCode);
    }

    [Fact]
    public async Task RemovingBothAssets_RemovesTheSharedRow()
    {
        var (client, accountId) = await AdminAsync();
        await PutAsync(client, LogoPath, TinyPng);
        await PutAsync(client, BannerPath, TinyPng);

        await DeleteAsync(client, LogoPath);
        await DeleteAsync(client, BannerPath);

        var rows = await factory.WithTenantScopeAsync(
            accountId, db => db.FarmLogos.CountAsync());
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task RemovingAnUnsetBanner_Returns404()
    {
        var (client, _) = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(client, BannerPath)).StatusCode);
    }

    [Theory]
    [InlineData(Roles.ReadOnly)]
    [InlineData(Roles.Sales)]
    public async Task NonAdmins_CannotChangeTheBanner(string role)
    {
        var (_, accountId) = await AdminAsync();
        var email = $"n-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(client, BannerPath, TinyPng)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(client, BannerPath)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_GetsNothing()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(BannerPath)).StatusCode);
    }

    // --- the banner's own error-code namespace (#179) -----------------------

    [Fact]
    public async Task OversizeBanner_UsesFarmBannerCode_NotFarmLogo()
    {
        var (client, _) = await AdminAsync();
        // Bigger than the banner's default 5 MB cap.
        var oversize = new byte[6 * 1024 * 1024];

        var response = await PutAsync(client, BannerPath, oversize);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("FarmBanner.TooLarge", problem!.Title);
    }

    [Fact]
    public async Task UnsupportedFormatBanner_UsesFarmBannerCode_NotFarmLogo()
    {
        var (client, _) = await AdminAsync();

        var response = await PutAsync(client, BannerPath, "not an image"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>();
        Assert.Equal("FarmBanner.UnsupportedFormat", problem!.Title);
    }

    private sealed record ProblemDto(string? Title);

    // --- the concurrency token (AGENTS.md: every mutation needs one of these) -

    [Fact]
    public async Task TwoBannerReplacementsAtOnce_CannotMixOneImageWithTheOthersLabels()
    {
        // Same shape as FarmLogoTests' equivalent for the logo side: two held-
        // open snapshots, not two HTTP calls (the test host would just
        // serialise those). ReplaceBanner bumps the row's shared Version
        // (#179's accepted tradeoff), so the second writer 409s instead of
        // silently mixing writer A's bytes with writer B's stale metadata.
        var (client, accountId) = await AdminAsync();
        await PutAsync(client, BannerPath, TinyPng);

        // Perturb a byte inside IDAT's compressed data (same index FarmLogoTests'
        // BuildTwinPng uses) — the walk copies pixel bytes through unread, so
        // this stays a valid container of identical shape and length.
        var twin = (byte[])TinyPng.Clone();
        twin[45] ^= 0xFF;

        var conflict = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, dbA =>
                factory.WithTenantScopeAsync(accountId, async dbB =>
                {
                    var a = await dbA.FarmLogos.FirstAsync();
                    var b = await dbB.FarmLogos.FirstAsync();

                    a.ReplaceBanner(
                        ImageSanitizer.Sanitize(twin, ImageSanitizer.MaxBannerByteLengthCeiling, ImageSanitizer.ImageAssetKind.Banner).Value,
                        DateTimeOffset.UtcNow);
                    await dbA.SaveChangesAsync();

                    b.ReplaceBanner(
                        ImageSanitizer.Sanitize(TinyPng, ImageSanitizer.MaxBannerByteLengthCeiling, ImageSanitizer.ImageAssetKind.Banner).Value,
                        DateTimeOffset.UtcNow);
                    await dbB.SaveChangesAsync();
                })));

        Assert.IsType<DbUpdateConcurrencyException>(conflict);
    }

    // --- surfaced on /account -----------------------------------------------

    [Fact]
    public async Task AccountRead_SurfacesTheBannerHash_IndependentlyOfTheLogo()
    {
        var (client, _) = await AdminAsync();
        await PutAsync(client, BannerPath, TinyPng);

        var account = await client.GetFromJsonAsync<AccountDto>(AccountPath);

        Assert.Null(account!.LogoContentHash);
        Assert.NotNull(account.BannerContentHash);
    }

    // --- fixtures ------------------------------------------------------------

    // A 1x1 RGBA PNG as an encoder produces it — same fixture as FarmLogoTests.
    private static readonly byte[] TinyPng = Convert.FromHexString(
        "89504E470D0A1A0A" +
        "0000000D49484452000000010000000108060000001F15C489" +
        "0000000A49444154789C63000100000500010D0A2DB4" +
        "0000000049454E44AE426082");
}
