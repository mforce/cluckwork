using System.Buffers.Binary;

namespace Cluckwork.Domain.Modules.Farm.Media;

public static partial class ImageSanitizer
{
    // --- shared ------------------------------------------------------------

    private static Result<SanitizedImage> Complete(
        ImageKind kind, byte[] output, int written, int width, int height, ImageAssetKind asset)
    {
        if (width <= 0 || height <= 0) return Result.Failure<SanitizedImage>(Malformed(asset));
        if (Exceeds(width, height)) return Result.Failure<SanitizedImage>(DimensionsTooLarge(asset));

        return Result.Success(new SanitizedImage(kind, output[..written], width, height));
    }

    private static bool Exceeds(int width, int height) =>
        width > MaxPixelDimension || height > MaxPixelDimension;

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
    private static readonly uint Plte = Fcc("PLTE");
    private static readonly uint Srgb = Fcc("sRGB");
    private static readonly uint Gama = Fcc("gAMA");
    private static readonly uint Chrm = Fcc("cHRM");
    private static readonly uint Phys = Fcc("pHYs");
    private static readonly uint Sbit = Fcc("sBIT");
    private static readonly uint Bkgd = Fcc("bKGD");
    private static readonly uint Trns = Fcc("tRNS");
    private static readonly uint Vp8 = Fcc("VP8 ");
    private static readonly uint Vp8L = Fcc("VP8L");
    private static readonly uint Vp8x = Fcc("VP8X");
    private static readonly uint Anim = Fcc("ANIM");
    private static readonly uint Anmf = Fcc("ANMF");
    private static readonly uint Actl = Fcc("acTL");
    private static readonly uint Fctl = Fcc("fcTL");
    private static readonly uint Fdat = Fcc("fdAT");
}
