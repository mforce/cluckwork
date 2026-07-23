namespace Cluckwork.Domain.Media;

using System.Buffers.Binary;
using System.Collections.Frozen;
using Cluckwork.Domain.Common;

// #123 — what a farm logo upload is allowed to be.
//
// This walks the container and rewrites it. It never decodes pixels, and that
// is the design, not a shortcut: decoding would put a third-party image codec
// in the request path (the BCL has had no cross-platform one since
// System.Drawing.Common went Windows-only in .NET 7) and hand an attacker-
// controlled buffer to it, and it is decoding that makes a decompression bomb
// expensive. Walking the container costs one pass over at most 1 MB and adds no
// dependency.
//
// What the walk buys, in order of how much it matters:
//
//  1. The format is decided by the leading bytes, never by the filename or the
//     client's Content-Type — both of which the uploader writes.
//  2. Everything after the format's own end marker is discarded. This is what
//     kills polyglots: the standard trick is a valid image with a ZIP, a shell
//     script or an HTML document glued to the tail, and every byte of that tail
//     is past IEND / EOI / the RIFF length.
//  3. Metadata containers are dropped, so a logo photographed on a phone stops
//     carrying the farm's GPS coordinates. The rule is an allowlist — a chunk
//     type nobody had invented when this was written is dropped by default
//     rather than carried by default.
//  4. Declared dimensions are read from the header and capped. This is the one
//     bomb vector the no-decode approach still has to answer for: our server
//     never allocates the pixels, but the browsers of everyone on the farm
//     would, and a 900 KB PNG can declare 30000x30000.
//
// What it deliberately does NOT promise: that the pixel data decodes. Bytes
// inside IDAT/entropy segments are copied through unread, so a corrupt image is
// stored and renders broken. That is a display bug, not a security one.
public static class ImageSanitizer
{
    // Generous for a logo — a 512x512 PNG is tens of KB — and small enough that
    // buffering the whole upload in memory is not itself the attack.
    public const int MaxByteLength = 1024 * 1024;

    // Above this a decode is measured in gigabytes of client RAM. 4096 is far
    // past any sane logo and still renders on a 4K display at 1:1.
    public const int MaxPixelDimension = 4096;

    public static readonly Error Empty = Error.Validation(
        "FarmLogo.Empty", "The uploaded file is empty.");

    public static readonly Error TooLarge = Error.Validation(
        "FarmLogo.TooLarge",
        $"The logo must be {MaxByteLength / 1024} KB or smaller.");

    public static readonly Error UnsupportedFormat = Error.Validation(
        "FarmLogo.UnsupportedFormat",
        "The logo must be a PNG, JPEG or WebP image. SVG is not accepted because it can carry script.");

    public static readonly Error Malformed = Error.Validation(
        "FarmLogo.Malformed",
        "The file claims to be an image but its structure is not valid. Re-export it and try again.");

    public static readonly Error DimensionsTooLarge = Error.Validation(
        "FarmLogo.DimensionsTooLarge",
        $"The logo must be at most {MaxPixelDimension}x{MaxPixelDimension} pixels.");

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Result<SanitizedImage> Sanitize(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return Result.Failure<SanitizedImage>(Empty);
        if (data.Length > MaxByteLength) return Result.Failure<SanitizedImage>(TooLarge);

        if (data.StartsWith(PngSignature)) return SanitizePng(data);

        // SOI. Every JPEG variant (JFIF, Exif, raw) shares it.
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return SanitizeJpeg(data);

        if (data.Length >= 12
            && FourCc(data) == Riff
            && FourCc(data[8..]) == Webp)
            return SanitizeWebp(data);

        return Result.Failure<SanitizedImage>(UnsupportedFormat);
    }

    // --- PNG ---------------------------------------------------------------
    //
    // Signature, then a chunk stream: length(4 BE) type(4) data CRC(4). CRCs are
    // copied verbatim along with their chunk, so nothing has to be recomputed —
    // another reason not to touch chunk contents.

    private static readonly FrozenSet<uint> PngKeep = new[]
    {
        // Critical — without these there is no image.
        "IHDR", "PLTE", "IDAT", "IEND",
        // Ancillary, but they change how the image LOOKS. iCCP especially: drop
        // a colour profile and the farm's brand colour shifts, which for a logo
        // feature is the one thing we must not silently do.
        "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs",
        // APNG. Animated WebP is accepted below, so an animated PNG is too.
        "acTL", "fcTL", "fdAT"
    }.Select(Fcc).ToFrozenSet();

    private static Result<SanitizedImage> SanitizePng(ReadOnlySpan<byte> data)
    {
        // The rewrite only ever drops, so the input length is a safe ceiling.
        var output = new byte[data.Length];
        PngSignature.CopyTo(output, 0);
        var written = PngSignature.Length;

        var pos = PngSignature.Length;
        int width = 0, height = 0;
        var sawHeader = false;
        var sawPixels = false;
        var sawEnd = false;

        while (pos + 8 <= data.Length)
        {
            var declared = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            // Long arithmetic throughout: a declared length near uint.MaxValue
            // overflows an int add and would wrap into a passing bounds check.
            var chunkEnd = (long)pos + 8 + declared + 4;
            if (chunkEnd > data.Length) return Result.Failure<SanitizedImage>(Malformed);

            var type = FourCc(data[(pos + 4)..]);
            var dataStart = pos + 8;
            var length = (int)declared;

            if (!sawHeader)
            {
                // IHDR is required to be the first chunk, and it is where the
                // dimensions live, so nothing can precede it.
                if (type != Ihdr || length < 13) return Result.Failure<SanitizedImage>(Malformed);
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[dataStart..]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(dataStart + 4)..]);
                sawHeader = true;
            }
            else if (type == Ihdr)
            {
                return Result.Failure<SanitizedImage>(Malformed);
            }

            if (type == Idat) sawPixels = true;

            // Bit 5 of the first byte clear = uppercase = critical. An unknown
            // critical chunk means no decoder can render the file, so storing it
            // would just be storing a broken logo.
            var isCritical = (data[pos + 4] & 0x20) == 0;
            if (!PngKeep.Contains(type) && isCritical)
                return Result.Failure<SanitizedImage>(Malformed);

            if (PngKeep.Contains(type))
            {
                data.Slice(pos, (int)(chunkEnd - pos)).CopyTo(output.AsSpan(written));
                written += (int)(chunkEnd - pos);
            }

            pos = (int)chunkEnd;

            // Stop at IEND and never look at what follows — see (2) above.
            if (type == Iend)
            {
                sawEnd = true;
                break;
            }
        }

        // Header, at least one pixel chunk, and a proper end. Same reasoning as
        // the unknown-critical-chunk rejection: a file missing any of these
        // cannot render, so accepting it only stores a broken logo.
        if (!sawHeader || !sawPixels || !sawEnd) return Result.Failure<SanitizedImage>(Malformed);
        return Complete(ImageKind.Png, output, written, width, height);
    }

    // --- JPEG --------------------------------------------------------------
    //
    // SOI, then marker segments (FF xx, 2-byte big-endian length that counts
    // itself), until SOS hands over to entropy-coded data. Progressive JPEGs
    // alternate between the two, so the walk below switches modes rather than
    // assuming SOS is the last thing it sees.

    private static Result<SanitizedImage> SanitizeJpeg(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        output[0] = 0xFF;
        output[1] = 0xD8;
        var written = 2;

        var pos = 2;
        int width = 0, height = 0;
        var sawFrame = false;
        var sawScan = false;
        var sawEnd = false;

        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF) return Result.Failure<SanitizedImage>(Malformed);

            // FF FF is legal padding before a marker.
            if (data[pos + 1] == 0xFF) { pos++; continue; }

            var marker = data[pos + 1];

            if (marker == 0xD9)
            {
                output[written++] = 0xFF;
                output[written++] = 0xD9;
                pos += 2;
                sawEnd = true;
                break;
            }

            // Standalone markers: TEM and the restart set carry no length.
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                output[written++] = 0xFF;
                output[written++] = marker;
                pos += 2;
                continue;
            }

            if (pos + 4 > data.Length) return Result.Failure<SanitizedImage>(Malformed);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            // The length counts its own two bytes, so anything under 2 is a lie
            // that would make the walk stand still or run backwards.
            if (segmentLength < 2) return Result.Failure<SanitizedImage>(Malformed);
            var segmentEnd = (long)pos + 2 + segmentLength;
            if (segmentEnd > data.Length) return Result.Failure<SanitizedImage>(Malformed);

            var payload = data.Slice(pos + 4, segmentLength - 2);

            // SOFn: every frame marker in C0..CF except DHT (C4), JPG (C8) and
            // DAC (CC). Payload is precision, height, width, components.
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (payload.Length < 5) return Result.Failure<SanitizedImage>(Malformed);
                height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                sawFrame = true;
            }

            if (KeepJpegSegment(marker, payload))
            {
                data.Slice(pos, (int)(segmentEnd - pos)).CopyTo(output.AsSpan(written));
                written += (int)(segmentEnd - pos);
            }

            pos = (int)segmentEnd;

            // After SOS the bytes are entropy-coded, not segments. Copy them
            // verbatim up to the next real marker.
            if (marker == 0xDA)
            {
                sawScan = true;
                var scanEnd = FindEndOfScan(data, pos);
                data.Slice(pos, scanEnd - pos).CopyTo(output.AsSpan(written));
                written += scanEnd - pos;
                pos = scanEnd;
            }
        }

        // A frame header for the dimensions, a scan for the pixels, and EOI.
        if (!sawFrame || !sawScan || !sawEnd) return Result.Failure<SanitizedImage>(Malformed);
        return Complete(ImageKind.Jpeg, output, written, width, height);
    }

    // Inside a scan, FF is escaped as FF 00, and restart markers are expected.
    // Any other marker ends the scan and returns control to the segment walker.
    private static int FindEndOfScan(ReadOnlySpan<byte> data, int start)
    {
        var i = start;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }

            var next = data[i + 1];
            if (next == 0x00 || next == 0xFF || next is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            return i;
        }

        return data.Length;
    }

    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload)
    {
        // APP1 is where EXIF and XMP live: camera model, timestamps, and the
        // GPS block. APP13 carries Photoshop/IPTC. Dropping the whole APPn
        // range except the two below is the allowlist.
        if (marker is >= 0xE0 and <= 0xEF)
        {
            // APP0/JFIF is pixel-density housekeeping, no personal data.
            if (marker == 0xE0) return true;
            // APP2 is usually an ICC colour profile. Same reasoning as iCCP in
            // PNG: dropping it shifts the brand colour.
            return marker == 0xE2 && payload.StartsWith("ICC_PROFILE\0"u8);
        }

        // COM is a free-text comment field.
        return marker != 0xFE;
    }

    // --- WebP --------------------------------------------------------------
    //
    // A RIFF container: "RIFF", 4-byte little-endian size covering everything
    // after those 8 bytes, "WEBP", then FourCC/size/payload chunks padded to an
    // even length.

    private static readonly FrozenSet<uint> WebpKeep = new[]
    {
        "VP8 ", "VP8L", "VP8X",   // the image itself, lossy / lossless / extended
        "ALPH",                   // alpha plane
        "ANIM", "ANMF",           // animation
        "ICCP"                    // colour profile — see the PNG note
    }.Select(Fcc).ToFrozenSet();

    private static Result<SanitizedImage> SanitizeWebp(ReadOnlySpan<byte> data)
    {
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        // The RIFF size is authoritative. A file longer than it declares has a
        // tail we were never meant to read, and one shorter is truncated.
        var end = 8L + declaredSize;
        if (end > data.Length || end < 12) return Result.Failure<SanitizedImage>(Malformed);
        var limit = (int)end;

        var output = new byte[data.Length];
        data[..12].CopyTo(output);
        var written = 12;

        var pos = 12;
        int width = 0, height = 0;
        var sawImage = false;
        var sawCanvas = false;
        var vp8xFlagsAt = -1;

        while (pos + 8 <= limit)
        {
            var type = FourCc(data[pos..]);
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            // Odd-length payloads are followed by one padding byte. Widened to
            // long first: uint.MaxValue + 1 wraps to zero, which would sail
            // through the bounds check below and then slice with a negative
            // length.
            var padded = (long)payloadLength + (payloadLength & 1);
            var chunkEnd = (long)pos + 8 + padded;
            if (chunkEnd > limit) return Result.Failure<SanitizedImage>(Malformed);

            var payload = data.Slice(pos + 8, (int)payloadLength);

            if (type == Vp8x)
            {
                if (payload.Length < 10) return Result.Failure<SanitizedImage>(Malformed);
                // Canvas size is stored minus one, as two 24-bit LE fields.
                width = ReadUInt24LittleEndian(payload[4..]) + 1;
                height = ReadUInt24LittleEndian(payload[7..]) + 1;
                sawImage = true;
                sawCanvas = true;
                // Remembered so the EXIF/XMP flag bits can be cleared after the
                // copy — a decoder that trusts them would go looking for chunks
                // this method has just removed.
                vp8xFlagsAt = written + 8;
            }
            else if (type == Vp8 || type == Vp8L)
            {
                int frameWidth = 0, frameHeight = 0;
                var read = type == Vp8
                    ? TryReadLossyDimensions(payload, ref frameWidth, ref frameHeight)
                    : TryReadLosslessDimensions(payload, ref frameWidth, ref frameHeight);
                if (!read) return Result.Failure<SanitizedImage>(Malformed);

                // VP8X carries the CANVAS size and comes first; a frame inside
                // it may be smaller. The canvas is what a decoder allocates, so
                // it is the number the cap has to judge — don't let the frame
                // overwrite it.
                if (!sawCanvas)
                {
                    width = frameWidth;
                    height = frameHeight;
                }

                sawImage = true;
            }

            if (WebpKeep.Contains(type))
            {
                data.Slice(pos, (int)(chunkEnd - pos)).CopyTo(output.AsSpan(written));
                written += (int)(chunkEnd - pos);
            }

            pos = (int)chunkEnd;
        }

        if (!sawImage) return Result.Failure<SanitizedImage>(Malformed);

        // VP8X flags, MSB first: Rsv Rsv ICC Alpha EXIF XMP Anim Rsv.
        if (vp8xFlagsAt >= 0) output[vp8xFlagsAt] &= 0xF3;

        // The chunks that were dropped came out of the middle, so the declared
        // size no longer matches what we are storing.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)(written - 8));

        return Complete(ImageKind.Webp, output, written, width, height);
    }

    // VP8 key frame: 3-byte tag, the 9D 01 2A start code, then 14-bit
    // dimensions each packed into a little-endian 16-bit field.
    private static bool TryReadLossyDimensions(ReadOnlySpan<byte> payload, ref int width, ref int height)
    {
        if (payload.Length < 10) return false;
        if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF;
        height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF;
        return true;
    }

    // VP8L: 0x2F signature, then 14 bits of width-1 and 14 of height-1 packed
    // into a little-endian 32-bit word.
    private static bool TryReadLosslessDimensions(ReadOnlySpan<byte> payload, ref int width, ref int height)
    {
        if (payload.Length < 5 || payload[0] != 0x2F) return false;
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
        width = (int)(bits & 0x3FFF) + 1;
        height = (int)((bits >> 14) & 0x3FFF) + 1;
        return true;
    }

    // --- shared ------------------------------------------------------------

    private static Result<SanitizedImage> Complete(
        ImageKind kind, byte[] output, int written, int width, int height)
    {
        if (width <= 0 || height <= 0) return Result.Failure<SanitizedImage>(Malformed);
        if (width > MaxPixelDimension || height > MaxPixelDimension)
            return Result.Failure<SanitizedImage>(DimensionsTooLarge);

        return Result.Success(new SanitizedImage(kind, output[..written], width, height));
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> b) =>
        b[0] | (b[1] << 8) | (b[2] << 16);

    private static uint FourCc(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32BigEndian(b);

    private static uint Fcc(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static readonly uint Riff = Fcc("RIFF");
    private static readonly uint Webp = Fcc("WEBP");
    private static readonly uint Ihdr = Fcc("IHDR");
    private static readonly uint Idat = Fcc("IDAT");
    private static readonly uint Iend = Fcc("IEND");
    private static readonly uint Vp8 = Fcc("VP8 ");
    private static readonly uint Vp8L = Fcc("VP8L");
    private static readonly uint Vp8x = Fcc("VP8X");
}
