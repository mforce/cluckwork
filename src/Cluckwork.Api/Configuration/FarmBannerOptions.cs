namespace Cluckwork.Api.Configuration;

using Cluckwork.Domain.Media;
using Microsoft.Extensions.Options;

// #179 — the OPERATIONAL cap on a farm-banner upload, tunable per deployment
// under the domain's hard ceiling (ImageSanitizer.MaxBannerByteLengthCeiling).
// Mirrors FarmLogoOptions exactly; kept separate because the banner (a wide,
// detailed hero image) is held to a different limit than the sidebar logo.
public sealed class FarmBannerOptions
{
    public const string SectionName = "FarmBanner";

    // 5 MB by default — a banner is expected to be a heavier, more detailed
    // image than the small sidebar logo mark.
    public int MaxUploadBytes { get; init; } = 5 * 1024 * 1024;
}

// Fail fast at startup, same reasoning as FarmLogoOptionsValidator.
public sealed class FarmBannerOptionsValidator : IValidateOptions<FarmBannerOptions>
{
    public ValidateOptionsResult Validate(string? name, FarmBannerOptions options)
    {
        if (options.MaxUploadBytes <= 0)
            return ValidateOptionsResult.Fail(
                $"{FarmBannerOptions.SectionName}:{nameof(FarmBannerOptions.MaxUploadBytes)} must be greater than zero.");

        if (options.MaxUploadBytes > ImageSanitizer.MaxBannerByteLengthCeiling)
            return ValidateOptionsResult.Fail(
                $"{FarmBannerOptions.SectionName}:{nameof(FarmBannerOptions.MaxUploadBytes)} " +
                $"({options.MaxUploadBytes}) cannot exceed the {ImageSanitizer.MaxBannerByteLengthCeiling}-byte ceiling.");

        return ValidateOptionsResult.Success;
    }
}
