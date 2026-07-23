namespace Cluckwork.Api.IntegrationTests;

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Media;
using Microsoft.EntityFrameworkCore;

// #123 slice 2 — the farm logo over the wire: who may upload one, what a stored
// image is allowed to be, and what comes back out.
//
// The point of the end-to-end cases here is that ImageSanitizer's guarantees
// survive the round trip through Postgres and back through the serve endpoint.
// A unit test proves the rewrite drops EXIF; these prove nobody stored the
// original alongside it.
[Collection(IntegrationCollection.Name)]
public sealed class FarmLogoTests(CluckworkWebApplicationFactory factory)
{
    private const string LogoPath = "/api/v1/account/logo";
    private const string AccountPath = "/api/v1/account";

    private sealed record LogoDto(
        string ContentType, string ContentHash, int Width, int Height, int ByteLength, DateTimeOffset UpdatedAt);
    private sealed record AccountDto(Guid Id, string Name, string? LogoContentHash);
    private sealed record ProblemDto(string? Title);

    private async Task<(HttpClient Client, Guid AccountId, string Email)> AdminAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        return (factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email)), accountId, email);
    }

    private static Task<HttpResponseMessage> PutLogoAsync(
        HttpClient client, byte[] bytes, string contentType = "image/png")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, LogoPath)
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DeleteLogoAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, LogoPath);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    // --- the happy path ----------------------------------------------------

    [Fact]
    public async Task Admin_UploadsALogo_AndEveryoneCanFetchIt()
    {
        var (client, accountId, _) = await AdminAsync();

        var upload = await PutLogoAsync(client, TinyPng);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = (await upload.Content.ReadFromJsonAsync<LogoDto>())!;
        Assert.Equal("image/png", meta.ContentType);
        Assert.Equal(1, meta.Width);
        Assert.Equal(1, meta.Height);
        Assert.Equal(TinyPng.Length, meta.ByteLength);

        // A read-only viewer sees farm branding like anyone else.
        var viewerEmail = $"v-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, viewerEmail, Roles.ReadOnly);
        var viewer = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(viewerEmail));

        var fetched = await viewer.GetAsync(LogoPath);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TinyPng, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TheServedTypeComesFromTheBytes_NotFromWhatTheClientClaimed()
    {
        var (client, _, _) = await AdminAsync();

        // Upload a genuine PNG while insisting it is a JPEG.
        var upload = await PutLogoAsync(client, TinyPng, contentType: "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var fetched = await client.GetAsync(LogoPath);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AnUnchangedLogo_ComesBackAsANotModified()
    {
        var (client, _, _) = await AdminAsync();
        await PutLogoAsync(client, TinyPng);

        var first = await client.GetAsync(LogoPath);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var conditional = new HttpRequestMessage(HttpMethod.Get, LogoPath);
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NoLogo_Is404_SoTheChromeKnowsToFallBack()
    {
        var (client, _, _) = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
    }

    [Fact]
    public async Task TheAccountPayload_SaysWhetherThereIsALogoAndWhichOne()
    {
        var (client, _, _) = await AdminAsync();

        var before = (await client.GetFromJsonAsync<AccountDto>(AccountPath))!;
        Assert.Null(before.LogoContentHash);

        var upload = await PutLogoAsync(client, TinyPng);
        var meta = (await upload.Content.ReadFromJsonAsync<LogoDto>())!;

        var after = (await client.GetFromJsonAsync<AccountDto>(AccountPath))!;
        Assert.Equal(meta.ContentHash, after.LogoContentHash);
    }

    [Fact]
    public async Task ReplacingTheLogo_ChangesTheHashAndTheBytes()
    {
        var (client, accountId, _) = await AdminAsync();
        await PutLogoAsync(client, TinyPng);
        var first = (await client.GetFromJsonAsync<AccountDto>(AccountPath))!.LogoContentHash;

        var replacement = await PutLogoAsync(client, JpegWith(Exif));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);

        var second = (await client.GetFromJsonAsync<AccountDto>(AccountPath))!.LogoContentHash;
        Assert.NotEqual(first, second);

        var fetched = await client.GetAsync(LogoPath);
        Assert.Equal("image/jpeg", fetched.Content.Headers.ContentType?.MediaType);

        // The BYTES, not just the metadata around them: keeping the old PNG
        // content while updating type, hash and dimensions satisfied every
        // other assertion in this test (codex review of #168).
        var served = await fetched.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(TinyPng, served);
        Assert.Equal(0xFF, served[0]);
        Assert.Equal(0xD8, served[1]);

        // Replace, not accumulate.
        var rows = await factory.WithTenantScopeAsync(accountId, db => db.FarmLogos.CountAsync());
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Delete_ClearsIt_AndDeletingNothingIs404()
    {
        var (client, _, _) = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await DeleteLogoAsync(client)).StatusCode);

        await PutLogoAsync(client, TinyPng);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteLogoAsync(client)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<AccountDto>(AccountPath))!.LogoContentHash);
    }

    [Fact]
    public async Task AWebpLogoMakesTheSameRoundTrip()
    {
        // The unit tests cover the WebP walk; this covers the third format
        // actually surviving the endpoint, the column and the serve path.
        var (client, _, _) = await AdminAsync();

        var upload = await PutLogoAsync(client, TinyWebp, contentType: "image/webp");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var meta = (await upload.Content.ReadFromJsonAsync<LogoDto>())!;
        Assert.Equal("image/webp", meta.ContentType);
        Assert.Equal(64, meta.Width);
        Assert.Equal(48, meta.Height);

        var fetched = await client.GetAsync(LogoPath);
        Assert.Equal("image/webp", fetched.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TinyWebp, await fetched.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TwoUploadsAtOnceLeaveExactlyOneLogo()
    {
        // Two uploads in flight at once must leave one logo, whether they
        // actually interleave or the host serialises them — this asserts the
        // OUTCOME, and does not claim to prove the unique index was reached.
        // ASecondLogoRowCannotExist below is what proves the constraint is real
        // (all four reviewers of #168 asked for concurrency cover; this is the
        // honest split between the two questions).
        var (client, accountId, _) = await AdminAsync();

        var results = await Task.WhenAll(
            PutLogoAsync(client, TinyPng),
            PutLogoAsync(client, JpegWith(Exif), contentType: "image/jpeg"));

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var r in results)
            Assert.True(
                r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"unexpected {(int)r.StatusCode} from a concurrent upload");

        var rows = await factory.WithTenantScopeAsync(accountId, db => db.FarmLogos.CountAsync());
        Assert.Equal(1, rows);

        // Whichever won, the row describes the image it actually holds — the
        // fields are rewritten together, so a mix of two uploads is not
        // reachable even under last-write-wins.
        var stored = await factory.WithTenantScopeAsync(accountId, db => db.FarmLogos
            .Select(l => new { l.Content, l.ContentType, l.ByteLength })
            .FirstAsync());
        Assert.Equal(stored.Content.Length, stored.ByteLength);
        Assert.Equal(stored.ContentType == "image/png" ? TinyPng.Length : stored.ByteLength, stored.ByteLength);
    }

    [Fact]
    public async Task ASecondLogoRowCannotExist()
    {
        // The constraint itself, driven straight at the database so the result
        // does not depend on whether two HTTP requests happened to interleave.
        // Without the unique index the handler's read-then-write would let two
        // first-uploads both insert.
        var (client, accountId, _) = await AdminAsync();
        await PutLogoAsync(client, TinyPng);

        var duplicate = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, async db =>
            {
                var image = ImageSanitizer.Sanitize(TinyPng).Value;
                db.FarmLogos.Add(FarmLogo.Create(
                    Guid.NewGuid(), accountId, SeedDefaults.FarmId, image, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }));

        Assert.IsAssignableFrom<DbUpdateException>(duplicate);

        var rows = await factory.WithTenantScopeAsync(accountId, db => db.FarmLogos.CountAsync());
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task AnAnimatedLogoIsRefused()
    {
        var (client, _, _) = await AdminAsync();
        var apng = (byte[])TinyPng.Clone();
        // Rename the IDAT chunk type to acTL — enough to make the walk see an
        // animation control chunk without building a whole APNG.
        "acTL"u8.CopyTo(apng.AsSpan(37));

        var response = await PutLogoAsync(client, apng);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FarmLogo.AnimationNotSupported",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    // --- what the stored image is allowed to be ----------------------------

    [Fact]
    public async Task AnSvgIsRefused_BecauseItCanCarryScript()
    {
        var (client, _, _) = await AdminAsync();
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        var response = await PutLogoAsync(client, svg, contentType: "image/svg+xml");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("FarmLogo.UnsupportedFormat",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
    }

    [Theory]
    [InlineData("GIF89a....")]
    [InlineData("%PDF-1.7")]
    [InlineData("<!DOCTYPE html><script>alert(1)</script>")]
    public async Task AnythingOutsideTheThreeFormatsIs415(string content)
    {
        var (client, _, _) = await AdminAsync();

        var response = await PutLogoAsync(client, Encoding.UTF8.GetBytes(content));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyBodyIs422()
    {
        var (client, _, _) = await AdminAsync();

        var response = await PutLogoAsync(client, []);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FarmLogo.Empty", (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    [Fact]
    public async Task ABrokenContainerIs422()
    {
        var (client, _, _) = await AdminAsync();
        // Right signature, chunk length pointing past the end of the file.
        var broken = (byte[])TinyPng.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(8), 0x0FFF_FFFF);

        var response = await PutLogoAsync(client, broken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FarmLogo.Malformed", (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    [Fact]
    public async Task AHeaderClaimingAGigapixelCanvasIs422()
    {
        var (client, _, _) = await AdminAsync();

        var response = await PutLogoAsync(client, PngDeclaring(30000, 30000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FarmLogo.DimensionsTooLarge",
            (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
    }

    [Fact]
    public async Task AnUploadDeclaringItIsOverTheCapIsRefusedBeforeItIsRead()
    {
        var (client, _, _) = await AdminAsync();
        var oversize = new byte[ImageSanitizer.MaxByteLength + 4096];
        TinyPng.CopyTo(oversize, 0);

        var response = await PutLogoAsync(client, oversize);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("FarmLogo.TooLarge", (await response.Content.ReadFromJsonAsync<ProblemDto>())!.Title);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
    }

    [Fact]
    public async Task AnUploadThatHidesItsSizeIsStillRefused()
    {
        // Content-Length is a claim. A chunked upload makes none, so the only
        // thing standing between the process and an unbounded body is the cap
        // on the read loop itself.
        var (client, _, _) = await AdminAsync();
        var oversize = new byte[ImageSanitizer.MaxByteLength + 4096];
        TinyPng.CopyTo(oversize, 0);

        var request = new HttpRequestMessage(HttpMethod.Put, LogoPath)
        {
            Content = new StreamContent(new MemoryStream(oversize))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Content.Headers.ContentLength = null;
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(LogoPath)).StatusCode);
    }

    [Fact]
    public async Task AnUploadExactlyAtTheCapIsStillJudgedOnItsContent()
    {
        // The boundary itself: at the cap the size check must pass and the
        // sanitizer must be the thing that decides. Padding a PNG to 1 MB puts
        // the padding after IEND, so this also lands on the truncation path.
        var (client, _, _) = await AdminAsync();
        var atCap = new byte[ImageSanitizer.MaxByteLength];
        TinyPng.CopyTo(atCap, 0);

        var response = await PutLogoAsync(client, atCap);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var meta = (await response.Content.ReadFromJsonAsync<LogoDto>())!;
        Assert.Equal(TinyPng.Length, meta.ByteLength);
    }

    // --- the sanitizer's guarantees, end to end ----------------------------

    [Fact]
    public async Task TheStoredJpegNoLongerCarriesTheCamerasExifBlock()
    {
        // A logo photographed or exported on a phone carries GPS coordinates,
        // which for a farm is its physical location. The unit tests prove the
        // rewrite drops them; this proves the ORIGINAL was never kept.
        var (client, accountId, _) = await AdminAsync();

        var upload = await PutLogoAsync(client, JpegWith(Exif), contentType: "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var served = await (await client.GetAsync(LogoPath)).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPSLatitude", AsBytewiseText(served));

        var stored = await factory.WithTenantScopeAsync(
            accountId, db => db.FarmLogos.Select(l => l.Content).FirstAsync());
        Assert.DoesNotContain("GPSLatitude", AsBytewiseText(stored));
        Assert.DoesNotContain("Exif", AsBytewiseText(stored));
    }

    [Fact]
    public async Task TheStoredPngNoLongerCarriesWhateverWasGluedToItsTail()
    {
        var (client, accountId, _) = await AdminAsync();
        var payload = "<html><script>alert(document.cookie)</script></html>"u8.ToArray();

        var upload = await PutLogoAsync(client, [.. TinyPng, .. payload]);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var stored = await factory.WithTenantScopeAsync(
            accountId, db => db.FarmLogos.Select(l => l.Content).FirstAsync());
        Assert.Equal(TinyPng, stored);
    }

    // --- who may do it -----------------------------------------------------

    [Theory]
    [InlineData(Roles.ReadOnly)]
    [InlineData(Roles.Sales)]
    public async Task NonAdmins_CannotChangeTheBranding(string role)
    {
        var (_, accountId, _) = await AdminAsync();
        var email = $"n-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        Assert.Equal(HttpStatusCode.Forbidden, (await PutLogoAsync(client, TinyPng)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteLogoAsync(client)).StatusCode);
    }

    [Fact]
    public async Task AWorkerCannotChangeTheBrandingEither()
    {
        var (_, accountId, _) = await AdminAsync();
        var email = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, asAdmin: false);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        Assert.Equal(HttpStatusCode.Forbidden, (await PutLogoAsync(client, TinyPng)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_GetsNothing()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(LogoPath)).StatusCode);
    }

    [Fact]
    public async Task OneFarmsLogoIsInvisibleToAnother()
    {
        var (first, _, _) = await AdminAsync();
        await PutLogoAsync(first, TinyPng);

        var (second, _, _) = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(LogoPath)).StatusCode);
        Assert.Null((await second.GetFromJsonAsync<AccountDto>(AccountPath))!.LogoContentHash);
    }

    // --- the trail ---------------------------------------------------------

    [Fact]
    public async Task BrandingChanges_AreOnTheAuditTrail()
    {
        var (client, accountId, _) = await AdminAsync();

        await PutLogoAsync(client, TinyPng);
        await DeleteLogoAsync(client);

        var actions = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == nameof(FarmLogo))
            .Select(e => e.Action)
            .ToListAsync());

        Assert.Contains("Account.SetLogo", actions);
        Assert.Contains("Account.RemoveLogo", actions);
    }

    [Fact]
    public async Task ARejectedUpload_LeavesNoRowAndNoAuditEntry()
    {
        var (client, accountId, _) = await AdminAsync();

        await PutLogoAsync(client, PngDeclaring(30000, 30000));

        var logos = await factory.WithTenantScopeAsync(accountId, db => db.FarmLogos.CountAsync());
        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(e => e.EntityType == nameof(FarmLogo)));

        Assert.Equal(0, logos);
        Assert.Equal(0, events);
    }

    // --- fixtures ----------------------------------------------------------

    // A 1x1 RGBA PNG as an encoder produces it.
    private static readonly byte[] TinyPng = Convert.FromHexString(
        "89504E470D0A1A0A" +
        "0000000D49484452000000010000000108060000001F15C489" +
        "0000000A49444154789C63000100000500010D0A2DB4" +
        "0000000049454E44AE426082");

    private static readonly byte[] Exif =
        [.. "Exif\0\0"u8, .. "GPSLatitude 51.5074 GPSLongitude -0.1278"u8];

    // RIFF/WEBP wrapping a single lossless bitstream chunk: 0x2F signature,
    // then width-1 in the low 14 bits and height-1 in the next 14.
    private static readonly byte[] TinyWebp = BuildWebp(64, 48);

    private static byte[] BuildWebp(int width, int height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1), (uint)(width - 1) | ((uint)(height - 1) << 14));

        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        "VP8L"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);

        var file = new byte[12 + chunk.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + chunk.Length));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        chunk.CopyTo(file, 12);
        return file;
    }

    private static byte[] JpegWith(byte[] app1) =>
    [
        0xFF, 0xD8,
        .. Segment(0xE1, app1),
        .. Segment(0xC0, [8, 0, 32, 0, 32, 1, 1, 0x11, 0]),   // SOF0, 32x32
        .. Segment(0xDA, [1, 0, 0, 0x3F, 0]),                 // SOS
        0x12, 0x34,                                            // entropy data
        0xFF, 0xD9
    ];

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    // Structurally sound, but its header claims more pixels than any client
    // should be asked to decode.
    private static byte[] PngDeclaring(int width, int height)
    {
        var png = (byte[])TinyPng.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        return png;
    }

    // Latin-1 maps every byte to exactly one char, so searching the decoded
    // string for an ASCII marker is a byte search — and a failure prints the
    // marker rather than an index.
    private static string AsBytewiseText(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
