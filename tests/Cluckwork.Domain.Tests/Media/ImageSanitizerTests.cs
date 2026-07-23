namespace Cluckwork.Domain.Tests.Media;

using System.Buffers.Binary;
using System.Text;
using Cluckwork.Domain.Media;

// #123 — what a farm logo upload is allowed to be.
//
// The fixtures are built byte by byte rather than checked in as files, because
// the interesting cases (a chunk length that runs past the buffer, a payload
// glued after the end marker, a header claiming 30000 pixels) are exactly the
// ones no encoder will produce for you.
//
// Chunk CRCs in the builders are left zero. ImageSanitizer never reads them —
// it copies chunks through whole, so it never has to recompute one — and
// RealPng below is an encoder-produced image that proves the walk works on real
// bytes and returns them untouched.
public sealed class ImageSanitizerTests
{
    // --- format gate -------------------------------------------------------

    [Fact]
    public void Rejects_Nothing()
    {
        var result = ImageSanitizer.Sanitize([]);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Empty, result.Error);
    }

    [Fact]
    public void Rejects_AnythingOverTheSizeCap()
    {
        var oversize = new byte[ImageSanitizer.MaxByteLength + 1];
        RealPng.CopyTo(oversize, 0);

        var result = ImageSanitizer.Sanitize(oversize);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.TooLarge, result.Error);
    }

    // The allowlist is by leading bytes. SVG is the one that matters: it is a
    // document that can carry <script>, and serving one back to the farm would
    // be stored XSS. The rest are here so the gate is shown to be an allowlist
    // and not an SVG special case.
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("GIF89a")]
    [InlineData("%PDF-1.7")]
    [InlineData("<!DOCTYPE html><html><body>hello</body></html>")]
    [InlineData("BM____")]
    [InlineData("just some text")]
    public void Rejects_AnythingThatIsNotOneOfTheThreeFormats(string content)
    {
        var result = ImageSanitizer.Sanitize(Encoding.UTF8.GetBytes(content));

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.UnsupportedFormat, result.Error);
    }

    [Fact]
    public void Rejects_AnSvgEvenWhenItLeadsWithWhitespace()
    {
        // A leading newline shifts the signature, and a sniffer that trimmed
        // first could still be talked into an image verdict. This one never
        // trims: byte 0 decides.
        var result = ImageSanitizer.Sanitize("\n\n  <svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.UnsupportedFormat, result.Error);
    }

    // --- PNG ---------------------------------------------------------------

    [Fact]
    public void Png_ComesBackByteForByteWhenThereIsNothingToStrip()
    {
        var result = ImageSanitizer.Sanitize(RealPng);

        Assert.True(result.IsSuccess);
        Assert.Equal(ImageKind.Png, result.Value.Kind);
        Assert.Equal("image/png", result.Value.ContentType);
        Assert.Equal(1, result.Value.Width);
        Assert.Equal(1, result.Value.Height);
        Assert.Equal(RealPng, result.Value.Content);
    }

    [Fact]
    public void Png_DropsMetadataChunks()
    {
        var secret = "farm at 51.5074N 0.1278W"u8.ToArray();
        var png = Png(
            PngChunk("IHDR", Ihdr(64, 64)),
            PngChunk("tEXt", secret),
            PngChunk("iTXt", secret),
            PngChunk("zTXt", secret),
            PngChunk("eXIf", secret),
            PngChunk("tIME", [7, 0xEA, 1, 1, 0, 0, 0]),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, secret));
        foreach (var dropped in new[] { "tEXt", "iTXt", "zTXt", "eXIf", "tIME" })
            Assert.False(Contains(result.Value.Content, Encoding.ASCII.GetBytes(dropped)), dropped);
    }

    [Fact]
    public void Png_KeepsTheChunksThatDecideHowItLooks()
    {
        // Dropping a colour profile shifts the brand colour. For a LOGO that is
        // the one silent change we must not make, so these are on the allowlist
        // even though they are ancillary like the metadata above.
        var png = Png(
            PngChunk("IHDR", Ihdr(64, 64)),
            PngChunk("sRGB", [0]),
            PngChunk("gAMA", [0, 1, 0x86, 0xA0]),
            PngChunk("iCCP", "profile\0\0deadbeef"u8.ToArray()),
            PngChunk("tRNS", [0, 1]),
            PngChunk("pHYs", [0, 0, 0x0B, 0x13, 0, 0, 0x0B, 0x13, 1]),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsSuccess);
        foreach (var kept in new[] { "sRGB", "gAMA", "iCCP", "tRNS", "pHYs" })
            Assert.True(Contains(result.Value.Content, Encoding.ASCII.GetBytes(kept)), kept);
    }

    [Fact]
    public void Png_RefusesAnimation()
    {
        // Refused rather than flattened to its first frame: silently turning
        // someone's animated logo into a still one is a surprise, and the rule
        // is the same for both formats (see Webp_RefusesAnimation).
        var png = Png(
            PngChunk("IHDR", Ihdr(8, 8)),
            PngChunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]),
            PngChunk("fcTL", new byte[26]),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("fdAT", [0, 0, 0, 1, 9]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.AnimationNotSupported, result.Error);
    }

    // The polyglot hole reopened from the inside. IEND is on the allowlist and
    // allowlisted chunks are copied WHOLE, so a declared length on the
    // terminator smuggles its payload through — and truncating at IEND does not
    // remove it, because it sits before the terminator, not after it.
    [Fact]
    public void Png_RefusesAPayloadHidingInsideTheEndChunk()
    {
        var payload = "<html><script>alert(1)</script></html>"u8.ToArray();
        var png = Png(
            PngChunk("IHDR", Ihdr(8, 8)),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", payload));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Png_RefusesAnOversizeHeaderChunk()
    {
        // IHDR is fixed at 13 bytes. A longer one is surplus that would be
        // copied through inside an allowlisted chunk, same as the IEND case.
        var png = Png(
            PngChunk("IHDR", [.. Ihdr(8, 8), .. "trailing junk"u8]),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    // The headline case for the whole no-decode approach. A polyglot is a valid
    // image with a second file glued to its tail; every byte of that tail lives
    // past IEND, so rewriting the container removes it without understanding it.
    [Fact]
    public void Png_DiscardsEverythingAfterTheEndMarker()
    {
        var payload = "<html><script>alert(document.cookie)</script></html>"u8.ToArray();
        var png = Png(
            PngChunk("IHDR", Ihdr(8, 8)),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []),
            payload);

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, payload));
        Assert.Equal(png.Length - payload.Length, result.Value.Content.Length);
    }

    [Fact]
    public void Png_RejectsAnUnknownCriticalChunk()
    {
        // Uppercase first letter = critical. No decoder can render a file with
        // a critical chunk it does not know, so storing one would only ever
        // produce a broken logo.
        var png = Png(
            PngChunk("IHDR", Ihdr(8, 8)),
            PngChunk("ZZZZ", [1]),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Theory]
    [InlineData("no IHDR")]
    [InlineData("no IDAT")]
    [InlineData("no IEND")]
    [InlineData("IHDR is not first")]
    [InlineData("two IHDRs")]
    public void Png_RejectsAStructureThatCannotRender(string shape)
    {
        var png = shape switch
        {
            "no IHDR" => Png(PngChunk("IDAT", [1]), PngChunk("IEND", [])),
            "no IDAT" => Png(PngChunk("IHDR", Ihdr(8, 8)), PngChunk("IEND", [])),
            "no IEND" => Png(PngChunk("IHDR", Ihdr(8, 8)), PngChunk("IDAT", [1])),
            "IHDR is not first" => Png(
                PngChunk("pHYs", new byte[9]), PngChunk("IHDR", Ihdr(8, 8)),
                PngChunk("IDAT", [1]), PngChunk("IEND", [])),
            _ => Png(
                PngChunk("IHDR", Ihdr(8, 8)), PngChunk("IHDR", Ihdr(9, 9)),
                PngChunk("IDAT", [1]), PngChunk("IEND", []))
        };

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Png_RejectsAChunkLengthThatRunsPastTheBuffer()
    {
        var png = Png(PngChunk("IHDR", Ihdr(8, 8)), PngChunk("IDAT", [1]), PngChunk("IEND", []));
        // Claim the IDAT chunk is far longer than the bytes that follow it.
        var idatAt = 8 + 12 + 13;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(idatAt), 0x0FFF_FFFF);

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Png_RejectsAChunkLengthThatWouldOverflowTheBoundsCheck()
    {
        // uint.MaxValue: added to a position in 32-bit arithmetic this wraps
        // negative and sails through a naive "end <= length" test.
        var png = Png(PngChunk("IHDR", Ihdr(8, 8)), PngChunk("IDAT", [1]), PngChunk("IEND", []));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8 + 12 + 13), uint.MaxValue);

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    // --- JPEG --------------------------------------------------------------

    [Fact]
    public void Jpeg_DropsExifWhichIsWhereTheGpsCoordinatesLive()
    {
        var exif = Concat("Exif\0\0"u8.ToArray(), "GPSLatitude 51.5074 GPSLongitude -0.1278"u8.ToArray());
        var jpeg = Jpeg(
            JpegSegment(0xE1, exif),
            Sof0(200, 100),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12, 0x34],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, "GPSLatitude"u8.ToArray()));
        Assert.False(Contains(result.Value.Content, "Exif\0\0"u8.ToArray()));
    }

    [Fact]
    public void Jpeg_DropsCommentsAndTheOtherApplicationSegments()
    {
        var comment = "shot on my phone at home"u8.ToArray();
        var photoshop = Concat("Photoshop 3.0\0"u8.ToArray(), "8BIM"u8.ToArray());
        var jpeg = Jpeg(
            JpegSegment(0xFE, comment),
            JpegSegment(0xED, photoshop),
            JpegSegment(0xEF, "vendor junk"u8.ToArray()),
            Sof0(10, 10),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, comment));
        Assert.False(Contains(result.Value.Content, "Photoshop 3.0"u8.ToArray()));
        Assert.False(Contains(result.Value.Content, "vendor junk"u8.ToArray()));
    }

    [Fact]
    public void Jpeg_KeepsTheIccProfile()
    {
        // Same reasoning as PNG's iCCP: an ICC profile is not personal data,
        // and dropping it moves the brand colour.
        var icc = Concat("ICC_PROFILE\0"u8.ToArray(), [1, 1, 0, 0, 0, 0x0C]);
        var jpeg = Jpeg(
            JpegSegment(0xE2, icc),
            Sof0(10, 10),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.True(Contains(result.Value.Content, "ICC_PROFILE\0"u8.ToArray()));
    }

    [Fact]
    public void Jpeg_DropsApp0BecauseJfifCanEmbedAThumbnail()
    {
        // A JFIF APP0 carries a thumbnail in its trailing bytes, and JFXX
        // exists for nothing else — a whole second image inside the segment
        // this strip is supposed to clean. Density is no loss: a logo is laid
        // out by CSS, not DPI (codex review of #168).
        var thumbnail = "RGB-THUMBNAIL-PIXELS"u8.ToArray();
        var jfifWithThumb = Concat(
            "JFIF\0"u8.ToArray(),
            [.. new byte[] { 1, 1, 0, 0, 1, 0, 1, 2, 2 }, .. thumbnail]);
        var jpeg = Jpeg(
            JpegSegment(0xE0, jfifWithThumb),
            Sof0(10, 10),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, thumbnail));
        Assert.False(Contains(result.Value.Content, "JFIF\0"u8.ToArray()));
    }

    // JPEG permits any run of FF as padding before a marker, so FF FF D9 is a
    // legal EOI. Skipping two bytes on a fill run ate the second FF -- the real
    // marker prefix -- and walked straight past the terminator, rejecting a
    // perfectly good file (codex review of #168).
    //
    // Both parities, deliberately. With an even number of fill bytes a
    // two-at-a-time skip still happens to land on the marker, so a fixture that
    // tested only that case passed with the bug still in place -- which is what
    // the first version of this test did.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Jpeg_AcceptsTheLegalFillBytesBeforeAMarker(int fillBytes)
    {
        var jpeg = Jpeg(
            Sof0(24, 16),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12, 0x34],
            [.. Enumerable.Repeat((byte)0xFF, fillBytes), 0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess, $"{fillBytes} fill byte(s) before EOI was rejected");
        Assert.Equal(24, result.Value.Width);
        Assert.Equal(16, result.Value.Height);
    }

    [Fact]
    public void Jpeg_DropsAnApp2ThatIsNotAnIccProfile()
    {
        // APP2 is kept only when it actually starts with the ICC marker —
        // otherwise it is just another vendor block to strip.
        var jpeg = Jpeg(
            JpegSegment(0xE2, "MPF\0not a colour profile"u8.ToArray()),
            Sof0(10, 10),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, "not a colour profile"u8.ToArray()));
    }

    [Fact]
    public void Jpeg_ReadsItsDimensionsFromTheFrameHeader()
    {
        var jpeg = Jpeg(
            Sof0(640, 480),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.Equal(640, result.Value.Width);
        Assert.Equal(480, result.Value.Height);
    }

    [Fact]
    public void Jpeg_DiscardsEverythingAfterTheEndMarker()
    {
        var payload = "PK a zip lives here"u8.ToArray();
        var jpeg = Jpeg(
            Sof0(10, 10),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12, 0x34],
            [0xFF, 0xD9],
            payload);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, payload));
    }

    [Fact]
    public void Jpeg_SurvivesAProgressiveFilesRepeatedScans()
    {
        // A progressive JPEG goes back to segment headers after a scan, so the
        // walk cannot assume SOS is the last thing it will see. Restart markers
        // and FF00 stuffing inside the entropy data must not be read as
        // segment boundaries either.
        var jpeg = Jpeg(
            JpegSegment(0xC2, [8, 0, 20, 0, 40, 1, 1, 0x11, 0]),   // SOF2, 40x20
            JpegSegment(0xC4, [0, 1, 2, 3]),                       // DHT
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0xAB, 0xFF, 0x00, 0xCD, 0xFF, 0xD0, 0x11],            // stuffing + RST0
            JpegSegment(0xC4, [0, 4, 5, 6]),                       // another DHT
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x22, 0xFF, 0x00, 0x33],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsSuccess);
        Assert.Equal(40, result.Value.Width);
        Assert.Equal(20, result.Value.Height);
    }

    [Fact]
    public void Jpeg_RejectsASegmentLengthThatWouldStallTheWalk()
    {
        // The length field counts its own two bytes, so a declared 0 or 1 means
        // the next read starts at or before where this one did. Left unchecked
        // that is an endless loop on an attacker-supplied file.
        var jpeg = Jpeg(Sof0(10, 10), JpegSegment(0xDA, [1]), [0xFF, 0xD9]);
        var sosAt = Array.IndexOf(jpeg, (byte)0xDA, 2) - 1;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sosAt + 2), 1);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Theory]
    [InlineData("no frame header")]
    [InlineData("no scan")]
    [InlineData("no end marker")]
    public void Jpeg_RejectsAStructureThatCannotRender(string shape)
    {
        var jpeg = shape switch
        {
            "no frame header" => Jpeg(JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]), [0x12], [0xFF, 0xD9]),
            "no scan" => Jpeg(Sof0(10, 10), [0xFF, 0xD9]),
            _ => Jpeg(Sof0(10, 10), JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]), [0x12])
        };

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    // --- WebP --------------------------------------------------------------

    [Fact]
    public void Webp_DropsExifAndXmpAndSaysSoInTheHeaderFlags()
    {
        // VP8X advertises which optional chunks are present. Removing EXIF and
        // XMP without clearing their bits would leave a decoder hunting for
        // chunks that are no longer there.
        const byte hasIccExifXmp = 0x20 | 0x08 | 0x04;
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(300, 200, hasIccExifXmp)),
            WebpChunk("ICCP", "colour profile"u8.ToArray()),
            WebpChunk("EXIF", "GPSLatitude 51.5074"u8.ToArray()),
            WebpChunk("XMP ", "<x:xmpmeta>author</x:xmpmeta>"u8.ToArray()),
            WebpChunk("VP8L", Vp8LPayload(300, 200)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, "GPSLatitude"u8.ToArray()));
        Assert.False(Contains(result.Value.Content, "xmpmeta"u8.ToArray()));
        Assert.True(Contains(result.Value.Content, "colour profile"u8.ToArray()));

        // Flags byte sits just past the VP8X chunk header at offset 12.
        var flags = result.Value.Content[20];
        Assert.Equal(0, flags & 0x08);
        Assert.Equal(0, flags & 0x04);
        Assert.Equal(0x20, flags & 0x20);
    }

    [Fact]
    public void Webp_RewritesTheContainerLengthAfterDroppingChunks()
    {
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(64, 64, 0x08)),
            WebpChunk("EXIF", new byte[64]),
            WebpChunk("VP8L", Vp8LPayload(64, 64)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsSuccess);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(result.Value.Content.AsSpan(4));
        Assert.Equal((uint)(result.Value.Content.Length - 8), declared);
    }

    [Fact]
    public void Webp_TreatsTheContainerLengthAsAuthoritative()
    {
        var payload = "trailing payload"u8.ToArray();
        var webp = Concat(Webp(WebpChunk("VP8L", Vp8LPayload(32, 32))), payload);

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsSuccess);
        Assert.False(Contains(result.Value.Content, payload));
    }

    [Fact]
    public void Webp_RejectsAContainerLengthLongerThanTheFile()
    {
        var webp = Webp(WebpChunk("VP8L", Vp8LPayload(32, 32)));
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), (uint)webp.Length * 4);

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Webp_RejectsAChunkLengthThatWrapsWhenPaddedToEven()
    {
        // uint.MaxValue is odd, so padding adds one and the sum wraps to zero.
        // A bounds check done in 32 bits would then pass, and the slice that
        // follows would be handed a negative length.
        var webp = Webp(WebpChunk("VP8L", Vp8LPayload(32, 32)));
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16), uint.MaxValue);

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Webp_MeasuresTheCanvasRatherThanTheFrame()
    {
        // A frame inside an extended file can be smaller than the canvas, and
        // it is the canvas a decoder allocates — so it is the canvas the size
        // cap has to judge.
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(1000, 800, 0)),
            WebpChunk("VP8L", Vp8LPayload(10, 10)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Value.Width);
        Assert.Equal(800, result.Value.Height);
    }

    [Fact]
    public void Webp_ReadsLosslessDimensions()
    {
        var result = ImageSanitizer.Sanitize(Webp(WebpChunk("VP8L", Vp8LPayload(512, 256))));

        Assert.True(result.IsSuccess);
        Assert.Equal(ImageKind.Webp, result.Value.Kind);
        Assert.Equal("image/webp", result.Value.ContentType);
        Assert.Equal(512, result.Value.Width);
        Assert.Equal(256, result.Value.Height);
    }

    [Fact]
    public void Webp_ReadsLossyDimensions()
    {
        var result = ImageSanitizer.Sanitize(Webp(WebpChunk("VP8 ", Vp8Payload(320, 240))));

        Assert.True(result.IsSuccess);
        Assert.Equal(320, result.Value.Width);
        Assert.Equal(240, result.Value.Height);
    }

    [Fact]
    public void Webp_RejectsAContainerWithNoImageChunk()
    {
        var result = ImageSanitizer.Sanitize(Webp(WebpChunk("EXIF", new byte[8])));

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Webp_RejectsAHeaderWithNoPixelsBehindIt()
    {
        // VP8X only declares a canvas and which optional chunks follow. Without
        // VP8 or VP8L there are no pixels, and a ten-byte file was being stored
        // and served as image/webp (codex review of #168).
        var result = ImageSanitizer.Sanitize(Webp(WebpChunk("VP8X", Vp8XPayload(64, 64, 0))));

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Webp_RejectsASecondBitstreamChunk()
    {
        // Only the first chunk after the WEBP FourCC is the image — libwebp
        // stops looking there. A second one is something a decoder will never
        // read, so there is no honest reason for it to be in the file.
        var webp = Webp(
            WebpChunk("VP8L", Vp8LPayload(64, 64)),
            WebpChunk("VP8L", Vp8LPayload(32, 32)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    // The cap turned inside out. A decoder acts on the FIRST bitstream while
    // the walk was recording the LAST, so declaring the bomb first and
    // something harmless second shipped the file with a 1x1 verdict. Both the
    // per-chunk cap and the duplicate rejection now stand between that and a
    // stored image; this pins the outcome rather than which guard gets there.
    [Fact]
    public void Webp_CannotBeTalkedIntoMeasuringTheHarmlessChunk()
    {
        var webp = Webp(
            WebpChunk("VP8L", Vp8LPayload(16384, 16384)),
            WebpChunk("VP8L", Vp8LPayload(1, 1)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Webp_RejectsASecondCanvasHeader()
    {
        // Same shape one level up: a small second VP8X masking a large first.
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(64, 64, 0)),
            WebpChunk("VP8X", Vp8XPayload(1, 1, 0)),
            WebpChunk("VP8L", Vp8LPayload(64, 64)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    [Fact]
    public void Webp_JudgesTheFrameEvenWhenTheCanvasIsTheNumberItReports()
    {
        // A tiny canvas does not excuse a huge frame. Every dimension the file
        // declares has to clear the cap, because we cannot know which one a
        // given decoder will act on.
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(1, 1, 0)),
            WebpChunk("VP8L", Vp8LPayload(16384, 16384)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.DimensionsTooLarge, result.Error);
    }

    [Fact]
    public void Webp_RefusesAnimation()
    {
        // An ANMF frame nests its own chunk stream, so the flat allowlist never
        // looks inside and metadata rides through in a chunk it never sees.
        // Sweeping that means recursing the walk into every frame; refusing
        // animation is the smaller surface (codex review of #168).
        var hidden = "GPSLatitude 51.5074"u8.ToArray();
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(64, 64, 0x02)),
            WebpChunk("ANIM", [0, 0, 0, 0, 0, 0]),
            WebpChunk("ANMF", [.. new byte[16], .. "JUNK"u8, .. new byte[4], .. hidden]),
            WebpChunk("VP8L", Vp8LPayload(64, 64)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.AnimationNotSupported, result.Error);
    }

    // --- the dimension cap -------------------------------------------------
    //
    // The one bomb vector a no-decode sanitizer still owns. Our server never
    // allocates the pixels, but every browser on the farm would: a header
    // claiming 30000x30000 is under a megabyte compressed and about 3.6 GB
    // decoded.

    [Fact]
    public void Png_RejectsAHeaderClaimingMorePixelsThanAnyoneCanDecode()
    {
        var png = Png(
            PngChunk("IHDR", Ihdr(30000, 30000)),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.DimensionsTooLarge, result.Error);
    }

    [Fact]
    public void Jpeg_RejectsAnOversizeFrame()
    {
        var jpeg = Jpeg(
            Sof0(9000, 40),
            JpegSegment(0xDA, [1, 0, 0, 0x3F, 0]),
            [0x12],
            [0xFF, 0xD9]);

        var result = ImageSanitizer.Sanitize(jpeg);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.DimensionsTooLarge, result.Error);
    }

    [Fact]
    public void Webp_RejectsAnOversizeCanvas()
    {
        var webp = Webp(
            WebpChunk("VP8X", Vp8XPayload(16000, 16000, 0)),
            WebpChunk("VP8L", Vp8LPayload(10, 10)));

        var result = ImageSanitizer.Sanitize(webp);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.DimensionsTooLarge, result.Error);
    }

    [Fact]
    public void ExactlyTheCapIsAccepted()
    {
        var png = Png(
            PngChunk("IHDR", Ihdr(ImageSanitizer.MaxPixelDimension, ImageSanitizer.MaxPixelDimension)),
            PngChunk("IDAT", [1, 2, 3]),
            PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ZeroPixelsIsNotAnImage()
    {
        var png = Png(PngChunk("IHDR", Ihdr(0, 0)), PngChunk("IDAT", [1]), PngChunk("IEND", []));

        var result = ImageSanitizer.Sanitize(png);

        Assert.True(result.IsFailure);
        Assert.Equal(ImageSanitizer.Malformed, result.Error);
    }

    // --- fixtures ----------------------------------------------------------

    // A 1x1 RGBA PNG as an encoder produces it, CRCs and all.
    private static readonly byte[] RealPng = Convert.FromHexString(
        "89504E470D0A1A0A" +                                    // signature
        "0000000D49484452000000010000000108060000001F15C489" +  // IHDR 1x1 RGBA
        "0000000A49444154789C63000100000500010D0A2DB4" +        // IDAT
        "0000000049454E44AE426082");                            // IEND

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Png(params byte[][] parts) =>
        Concat(PngSignature, parts.SelectMany(p => p).ToArray());

    private static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;   // CRC left zero — see the class comment.
    }

    private static byte[] Ihdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)height);
        data[8] = 8;    // bit depth
        data[9] = 6;    // colour type: RGBA
        return data;
    }

    private static byte[] Jpeg(params byte[][] parts) =>
        Concat([0xFF, 0xD8], parts.SelectMany(p => p).ToArray());

    private static byte[] JpegSegment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    // SOF0: precision, height, width, component count, then one component.
    private static byte[] Sof0(int width, int height) => JpegSegment(0xC0,
    [
        8,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        1, 1, 0x11, 0
    ]);

    private static byte[] Webp(params byte[][] chunks)
    {
        var body = chunks.SelectMany(c => c).ToArray();
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);
        return file;
    }

    private static byte[] WebpChunk(string fourCc, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    // Flags, 3 reserved bytes, then canvas width-1 and height-1 as 24-bit LE.
    private static byte[] Vp8XPayload(int width, int height, byte flags)
    {
        var payload = new byte[10];
        payload[0] = flags;
        var w = width - 1;
        var h = height - 1;
        payload[4] = (byte)w; payload[5] = (byte)(w >> 8); payload[6] = (byte)(w >> 16);
        payload[7] = (byte)h; payload[8] = (byte)(h >> 8); payload[9] = (byte)(h >> 16);
        return payload;
    }

    // 0x2F signature, then width-1 in the low 14 bits and height-1 in the next.
    private static byte[] Vp8LPayload(int width, int height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(1), (uint)(width - 1) | ((uint)(height - 1) << 14));
        return payload;
    }

    // 3-byte frame tag, the 9D 01 2A start code, then 14-bit dimensions.
    private static byte[] Vp8Payload(int width, int height)
    {
        var payload = new byte[10];
        payload[3] = 0x9D; payload[4] = 0x01; payload[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)height);
        return payload;
    }

    private static byte[] Concat(byte[] first, byte[] second) => [.. first, .. second];

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
