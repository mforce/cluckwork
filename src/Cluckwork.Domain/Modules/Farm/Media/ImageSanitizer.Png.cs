using System.Buffers.Binary;
using System.Collections.Frozen;

namespace Cluckwork.Domain.Modules.Farm.Media;

public static partial class ImageSanitizer
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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
        "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs"
        // APNG's acTL/fcTL/fdAT are deliberately absent — animation is refused
        // outright, in both formats. See AnimationNotSupported.
    }.Select(Fcc).ToFrozenSet();

    // The size each allowlisted chunk is allowed to declare.
    //
    // Where the PNG specification fixes or bounds a chunk's size, this enforces
    // it, and surplus bytes are refused rather than copied. Two entries are
    // deliberately absent because their size is genuinely variable:
    //
    //   IDAT — compressed pixel data.
    //   iCCP — a compressed colour profile.
    //
    // Arbitrary bytes CAN ride inside those two, and no sanitizer that declines
    // to decode can prevent that (pixel data can carry anything by
    // construction). That is an accepted limit, not an oversight: the response
    // is served with a sniffed Content-Type under X-Content-Type-Options:
    // nosniff and the #144 CSP, so an inert blob is not an execution vector,
    // and uploads are Owner-only (#729). What this table stops is the case
    // where the surplus is unambiguous because the specification left no room
    // for it.
    private static bool PngChunkLengthIsLegal(uint type, int length)
    {
        if (type == Ihdr) return length == 13;
        if (type == Iend) return length == 0;
        if (type == Srgb) return length == 1;
        if (type == Gama) return length == 4;
        if (type == Chrm) return length == 32;
        if (type == Phys) return length == 9;
        if (type == Sbit) return length is >= 1 and <= 4;
        if (type == Bkgd) return length is 1 or 2 or 6;
        // Palette entries are RGB triples, 256 of them at most.
        if (type == Plte) return length is > 0 and <= 768 && length % 3 == 0;
        // Alpha for at most a full palette, or a single colour key.
        if (type == Trns) return length is > 0 and <= 256;
        return true;
    }

    private static Result<SanitizedImage> SanitizePng(ReadOnlySpan<byte> data, ImageAssetKind asset)
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
            if (chunkEnd > data.Length) return Result.Failure<SanitizedImage>(Malformed(asset));

            var type = FourCc(data[(pos + 4)..]);
            var dataStart = pos + 8;
            var length = (int)declared;

            // Length first, so the header read below is against a chunk whose
            // size the specification agrees with.
            if (!PngChunkLengthIsLegal(type, length))
                return Result.Failure<SanitizedImage>(Malformed(asset));

            if (!sawHeader)
            {
                // IHDR is required to be the first chunk, and it is where the
                // dimensions live, so nothing can precede it.
                if (type != Ihdr) return Result.Failure<SanitizedImage>(Malformed(asset));
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[dataStart..]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(dataStart + 4)..]);
                sawHeader = true;
            }
            else if (type == Ihdr)
            {
                return Result.Failure<SanitizedImage>(Malformed(asset));
            }

            // Animation is refused rather than silently flattened — see the
            // WebP walk for why the two formats are held to one rule.
            if (type == Actl || type == Fctl || type == Fdat)
                return Result.Failure<SanitizedImage>(AnimationNotSupported(asset));

            if (type == Idat) sawPixels = true;

            // Bit 5 of the first byte clear = uppercase = critical. An unknown
            // critical chunk means no decoder can render the file, so storing it
            // would just be storing a broken logo.
            var isCritical = (data[pos + 4] & 0x20) == 0;
            if (!PngKeep.Contains(type) && isCritical)
                return Result.Failure<SanitizedImage>(Malformed(asset));

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
        if (!sawHeader || !sawPixels || !sawEnd) return Result.Failure<SanitizedImage>(Malformed(asset));
        return Complete(ImageKind.Png, output, written, width, height, asset);
    }
}
