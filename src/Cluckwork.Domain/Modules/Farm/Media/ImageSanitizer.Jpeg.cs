using System.Buffers.Binary;

namespace Cluckwork.Domain.Modules.Farm.Media;

public static partial class ImageSanitizer
{
    // --- JPEG --------------------------------------------------------------
    //
    // SOI, then marker segments (FF xx, 2-byte big-endian length that counts
    // itself), until SOS hands over to entropy-coded data. Progressive JPEGs
    // alternate between the two, so the walk below switches modes rather than
    // assuming SOS is the last thing it sees.

    private static Result<SanitizedImage> SanitizeJpeg(ReadOnlySpan<byte> data, ImageAssetKind asset)
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
            if (data[pos] != 0xFF) return Result.Failure<SanitizedImage>(Malformed(asset));

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

            if (pos + 4 > data.Length) return Result.Failure<SanitizedImage>(Malformed(asset));
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            // The length counts its own two bytes, so anything under 2 is a lie
            // that would make the walk stand still or run backwards.
            if (segmentLength < 2) return Result.Failure<SanitizedImage>(Malformed(asset));
            var segmentEnd = (long)pos + 2 + segmentLength;
            if (segmentEnd > data.Length) return Result.Failure<SanitizedImage>(Malformed(asset));

            var payload = data.Slice(pos + 4, segmentLength - 2);

            // SOFn: every frame marker in C0..CF except DHT (C4), JPG (C8) and
            // DAC (CC). Payload is precision, height, width, components.
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (payload.Length < 5) return Result.Failure<SanitizedImage>(Malformed(asset));

                // Exactly the WebP duplicate-bitstream bug, in the format I
                // fixed it for and then left standing here (codex round 2 of
                // #168). Every SOF overwrote the dimensions and only the last
                // pair was capped, so SOF0(65535x65535) followed by SOF0(1x1)
                // reported 1x1 and shipped the bomb header. A still image has
                // one frame header.
                if (sawFrame) return Result.Failure<SanitizedImage>(Malformed(asset));

                height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                if (Exceeds(width, height)) return Result.Failure<SanitizedImage>(DimensionsTooLarge(asset));
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
        if (!sawFrame || !sawScan || !sawEnd) return Result.Failure<SanitizedImage>(Malformed(asset));
        return Complete(ImageKind.Jpeg, output, written, width, height, asset);
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

            // A fill byte, not a marker. Advance ONE, so the FF just examined is
            // discarded and the next one is still available as a marker prefix.
            // Skipping two here consumed the real prefix, and `FF FF D9` — a
            // legal way to write EOI — was walked straight past, leaving a
            // perfectly good JPEG rejected as malformed (codex review of #168).
            if (next == 0xFF)
            {
                i++;
                continue;
            }

            // FF 00 is a stuffed FF inside entropy data; RSTn are restart
            // markers. Both are two bytes and neither ends the scan.
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
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
            // APP0 is NOT kept, despite being "just" JFIF density housekeeping:
            // a JFIF APP0 can embed a thumbnail, and a JFXX APP0 exists only to
            // carry one. That is a whole second image riding inside the segment
            // this strip is meant to clean (codex review of #168). Density is no
            // loss either way — a logo is laid out by CSS, not by DPI.
            //
            // APP2 survives only when it claims to be an ICC colour profile.
            // Same reasoning as iCCP in PNG: dropping that shifts the brand
            // colour, which for a logo is the one silent change to avoid.
            //
            // This is a PREFIX check, not validation — an ICC profile has no
            // fixed size, so the bytes after the marker are unbounded and could
            // be anything. That is the accepted variable-length channel
            // described in the header comment, not an oversight.
            return marker == 0xE2 && payload.StartsWith("ICC_PROFILE\0"u8);
        }

        // COM is a free-text comment field.
        return marker != 0xFE;
    }
}
