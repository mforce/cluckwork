
namespace Cluckwork.Domain.Modules.Farm.Media;

// #123 — what a farm logo upload is allowed to be.
//
// This walks the container and rewrites it. It never decodes pixels, and that
// is the design, not a shortcut: decoding would put a third-party image codec
// in the request path (the BCL has had no cross-platform one since
// System.Drawing.Common went Windows-only in .NET 7) and hand an attacker-
// controlled buffer to it, and it is decoding that makes a decompression bomb
// expensive. Walking the container costs one linear pass over the upload — a
// few MB at the configured cap, never above the 5 MB ceiling below — and adds
// no dependency.
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
//     rather than carried by default — and every kept chunk whose size the
//     specification fixes is held to it, because a keep-listed chunk is copied
//     WHOLE and its surplus would ride along.
//
//     The limit, stated rather than glossed: three kept chunks are variable by
//     nature — PNG IDAT, PNG iCCP, and a JPEG ICC APP2 — and arbitrary bytes
//     can sit inside them. No sanitizer that declines to decode can prevent
//     that; pixel data can carry anything by construction. It is accepted here
//     because the response is served with a sniffed Content-Type under nosniff
//     and the #144 CSP, so an inert blob is not an execution vector, and
//     because uploads are Owner-only (#729).
//  4. Declared dimensions are read from the header and capped. This is the one
//     bomb vector the no-decode approach still has to answer for: our server
//     never allocates the pixels, but the browsers of everyone on the farm
//     would, and a 900 KB PNG can declare 30000x30000.
//
// What it deliberately does NOT promise: that the pixel data decodes. Bytes
// inside IDAT/entropy segments are copied through unread, so a corrupt image is
// stored and renders broken. That is a display bug, not a security one.

public static partial class ImageSanitizer
{
    // Which of the two independent images (#179: logo vs banner) a Sanitize
    // call is judging, so a failure names the right one — "FarmBanner.TooLarge"
    // and "The banner must be...", never the logo's code/copy for a banner
    // upload or vice versa. Code is what MapFailure switches on; Noun is what
    // the message reads.
    public readonly record struct ImageAssetKind(string Code, string Noun)
    {
        public static readonly ImageAssetKind Logo = new("FarmLogo", "logo");
        public static readonly ImageAssetKind Banner = new("FarmBanner", "banner");
    }

    // The HARD ceiling: the most the stored-bytes column will ever physically
    // tolerate, backing the DB check constraint (FarmLogoConfiguration) and the
    // upper bound the configured operational limit is validated against
    // (FarmLogoOptions). It is a schema invariant, not a policy knob — a
    // constraint is baked into the schema and cannot read config, so it holds
    // the ceiling while config sets the day-to-day limit under it.
    //
    // The OPERATIONAL limit — what an upload is actually held to — is the
    // `maxByteLength` argument to Sanitize, supplied from config by the
    // endpoint. It defaults to the ceiling only so a caller that does not care
    // about size (the format/metadata tests) need not state one; the endpoint
    // always passes the configured value.
    //
    // 5 MB is generous for a logo — a 512x512 PNG is tens of KB — and small
    // enough that buffering one upload in memory is not itself the attack.
    public const int MaxByteLengthCeiling = 5 * 1024 * 1024;

    // The banner (#179) is a wide/hero image, typically heavier than a square
    // mark, and gets its own ceiling rather than sharing the logo's — backing
    // FarmBannerConfiguration's check constraint and FarmBannerOptions.
    public const int MaxBannerByteLengthCeiling = 15 * 1024 * 1024;

    // Above this a decode is measured in gigabytes of client RAM. 4096 is far
    // past any sane logo/banner and still renders on a 4K display at 1:1. Not
    // an aspect-ratio rule — a wide banner just needs one dimension near this
    // ceiling and the other small.
    public const int MaxPixelDimension = 4096;

    // Every error below has a Logo-defaulting zero-arg overload (used by the
    // 50+ existing tests and callers that only ever sanitized a logo, before
    // #179 added a second caller) and an ImageAssetKind overload so a banner
    // upload gets its own code/copy instead of borrowing the logo's (#179).
    public static Error Empty() => Empty(ImageAssetKind.Logo);

    public static Error Empty(ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.Empty", "The uploaded file is empty.");

    // A factory, not a field: the limit is configurable now, so the message has
    // to carry whatever it was actually held to. The CODE stays "<Asset>.TooLarge"
    // so the status mapping (413) and the SPA's switch on it do not move.
    public static Error TooLarge(int maxByteLength) => TooLarge(maxByteLength, ImageAssetKind.Logo);

    public static Error TooLarge(int maxByteLength, ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.TooLarge",
        $"The {asset.Noun} must be {maxByteLength / 1024} KB or smaller.");

    public static Error UnsupportedFormat() => UnsupportedFormat(ImageAssetKind.Logo);

    public static Error UnsupportedFormat(ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.UnsupportedFormat",
        $"The {asset.Noun} must be a PNG, JPEG or WebP image. SVG is not accepted because it can carry script.");

    public static Error Malformed() => Malformed(ImageAssetKind.Logo);

    public static Error Malformed(ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.Malformed",
        "The file claims to be an image but its structure is not valid. Re-export it and try again.");

    public static Error DimensionsTooLarge() => DimensionsTooLarge(ImageAssetKind.Logo);

    public static Error DimensionsTooLarge(ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.DimensionsTooLarge",
        $"The {asset.Noun} must be at most {MaxPixelDimension}x{MaxPixelDimension} pixels.");

    // Animated PNG and animated WebP are both refused.
    //
    // WebP forces the decision: an ANMF frame carries its OWN nested chunk
    // stream, so a flat allowlist never looks inside it and metadata rides
    // through untouched in a JUNK chunk (codex review of #168). Sweeping that
    // means recursing the walk into every frame.
    //
    // APNG has no such nesting and could safely have been kept. It is refused
    // anyway so the product rule is one sentence — a logo is a still image —
    // rather than a per-format footnote, and so the surface stays small.
    public static Error AnimationNotSupported() => AnimationNotSupported(ImageAssetKind.Logo);

    public static Error AnimationNotSupported(ImageAssetKind asset) => Error.Validation(
        $"{asset.Code}.AnimationNotSupported",
        $"The {asset.Noun} must be a still image. Animated PNG and animated WebP are not accepted.");

    public static Result<SanitizedImage> Sanitize(
        ReadOnlySpan<byte> data, int maxByteLength = MaxByteLengthCeiling) =>
        Sanitize(data, maxByteLength, ImageAssetKind.Logo);

    // #179 — the banner shares this walk (format detection, security limits,
    // dimension cap) with the logo; only WHICH error codes/copy come back on
    // failure differs, via `asset`.
    public static Result<SanitizedImage> Sanitize(
        ReadOnlySpan<byte> data, int maxByteLength, ImageAssetKind asset)
    {
        if (data.Length == 0) return Result.Failure<SanitizedImage>(Empty(asset));
        if (data.Length > maxByteLength) return Result.Failure<SanitizedImage>(TooLarge(maxByteLength, asset));

        if (data.StartsWith(PngSignature)) return SanitizePng(data, asset);

        // SOI. Every JPEG variant (JFIF, Exif, raw) shares it.
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return SanitizeJpeg(data, asset);

        if (data.Length >= 12
            && FourCc(data) == Riff
            && FourCc(data[8..]) == Webp)
            return SanitizeWebp(data, asset);

        return Result.Failure<SanitizedImage>(UnsupportedFormat(asset));
    }
}
