using System.Buffers.Binary;
using System.Collections.Frozen;

namespace Cluckwork.Domain.Modules.Farm.Media;

public static partial class ImageSanitizer
{
    // --- WebP --------------------------------------------------------------
    //
    // A RIFF container: "RIFF", 4-byte little-endian size covering everything
    // after those 8 bytes, "WEBP", then FourCC/size/payload chunks padded to an
    // even length.

    private static readonly FrozenSet<uint> WebpKeep = new[]
    {
        "VP8 ", "VP8L", "VP8X",   // the image itself, lossy / lossless / extended
        "ALPH",                   // alpha plane
        "ICCP"                    // colour profile — variable size, see PngChunkLengthIsLegal
        // ANIM/ANMF are absent by design: an ANMF frame nests its own chunk
        // stream, which a flat allowlist cannot sweep. Animation is refused.
    }.Select(Fcc).ToFrozenSet();

    private static Result<SanitizedImage> SanitizeWebp(ReadOnlySpan<byte> data, ImageAssetKind asset)
    {
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        // The RIFF size is authoritative. A file longer than it declares has a
        // tail we were never meant to read, and one shorter is truncated.
        var end = 8L + declaredSize;
        if (end > data.Length || end < 12) return Result.Failure<SanitizedImage>(Malformed(asset));
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
            if (chunkEnd > limit) return Result.Failure<SanitizedImage>(Malformed(asset));

            var payload = data.Slice(pos + 8, (int)payloadLength);

            if (type == Anim || type == Anmf)
                return Result.Failure<SanitizedImage>(AnimationNotSupported(asset));

            if (type == Vp8x)
            {
                // A second VP8X would overwrite the first one's canvas. A
                // decoder reads the first; we would report the second.
                if (sawCanvas) return Result.Failure<SanitizedImage>(Malformed(asset));
                // EXACTLY ten: flags, three reserved, then two 24-bit canvas
                // fields. VP8X is on the keep-list and keep-listed chunks are
                // copied whole, so a longer one smuggles its surplus through —
                // the same hole as PNG's fixed-size chunks, checked here with
                // `<` instead of `!=` (adversarial review, round 2 of #168).
                if (payload.Length != 10) return Result.Failure<SanitizedImage>(Malformed(asset));
                // Canvas size is stored minus one, as two 24-bit LE fields.
                var canvasWidth = ReadUInt24LittleEndian(payload[4..]) + 1;
                var canvasHeight = ReadUInt24LittleEndian(payload[7..]) + 1;
                if (Exceeds(canvasWidth, canvasHeight))
                    return Result.Failure<SanitizedImage>(DimensionsTooLarge(asset));

                width = canvasWidth;
                height = canvasHeight;
                sawCanvas = true;
                // Remembered so the EXIF/XMP flag bits can be cleared after the
                // copy — a decoder that trusts them would go looking for chunks
                // this method has just removed.
                vp8xFlagsAt = written + 8;
            }
            else if (type == Vp8 || type == Vp8L)
            {
                // Only the FIRST bitstream chunk is the image. libwebp reads the
                // one following the WEBP FourCC and stops looking; a second
                // would be ignored by the decoder but was still overwriting the
                // dimensions we judge. That turned the cap inside out — declare
                // 16384x16384 first and 1x1 second and the bomb shipped with a
                // 1x1 verdict (adversarial review of #168).
                if (sawImage) return Result.Failure<SanitizedImage>(Malformed(asset));

                int frameWidth = 0, frameHeight = 0;
                var read = type == Vp8
                    ? TryReadLossyDimensions(payload, ref frameWidth, ref frameHeight)
                    : TryReadLosslessDimensions(payload, ref frameWidth, ref frameHeight);
                if (!read) return Result.Failure<SanitizedImage>(Malformed(asset));

                // Judged on its own account, whether or not it is the number we
                // go on to report. EVERY dimension the file declares has to
                // clear the cap, because we cannot know which one a given
                // decoder will act on.
                if (Exceeds(frameWidth, frameHeight))
                    return Result.Failure<SanitizedImage>(DimensionsTooLarge(asset));

                // VP8X carries the CANVAS size, which is what a decoder
                // allocates; a frame inside it may be smaller.
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

        // VP8X alone is not an image: it only declares a canvas and which
        // optional features follow. Actual pixels live in VP8 or VP8L, and
        // without one there is nothing to render (codex review of #168).
        if (!sawImage) return Result.Failure<SanitizedImage>(Malformed(asset));

        // VP8X flags, MSB first: Rsv Rsv ICC Alpha EXIF XMP Anim Rsv.
        //
        // EXIF (0x08) and XMP (0x04) go because those chunks were dropped, and
        // Anim (0x02) goes with them: a VP8X can advertise animation while
        // carrying no ANIM chunk, which slips past the rejection above (there
        // is nothing there to reject) and leaves the stored file claiming
        // frames it does not have — something decoders are entitled to treat as
        // an error (pi and codex, round 2 of #168).
        if (vp8xFlagsAt >= 0) output[vp8xFlagsAt] &= 0xF1;

        // The chunks that were dropped came out of the middle, so the declared
        // size no longer matches what we are storing.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)(written - 8));

        return Complete(ImageKind.Webp, output, written, width, height, asset);
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
}
