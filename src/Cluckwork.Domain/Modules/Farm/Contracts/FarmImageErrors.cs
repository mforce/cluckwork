using Cluckwork.Domain.Modules.Farm.Media;

namespace Cluckwork.Domain.Modules.Farm.Contracts;

// #1116: the size refusals an upload endpoint raises before the bytes reach the sanitizer, one per asset, so
// adapters name neither ImageSanitizer nor its asset kinds. The code, message and limit come from
// ImageSanitizer.TooLarge.
public static class FarmImageErrors
{
    public static Error LogoTooLarge(int maxByteLength) =>
        ImageSanitizer.TooLarge(maxByteLength, ImageSanitizer.ImageAssetKind.Logo);

    public static Error BannerTooLarge(int maxByteLength) =>
        ImageSanitizer.TooLarge(maxByteLength, ImageSanitizer.ImageAssetKind.Banner);
}
