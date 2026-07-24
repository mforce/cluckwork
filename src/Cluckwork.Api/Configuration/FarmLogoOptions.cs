namespace Cluckwork.Api.Configuration;

using Cluckwork.Domain.Media;
using Microsoft.Extensions.Options;

// #123 — the OPERATIONAL cap on a farm-logo upload, tunable per deployment
// under the domain's hard ceiling (ImageSanitizer.MaxByteLengthCeiling). The
// endpoint reads it, holds the upload to it, surfaces it to the SPA so the
// client-side pre-check and the "up to N MB" copy cannot drift from it, and
// passes it to the sanitizer as the size the bytes are judged against.
//
// The ceiling stays a constant, and the DB constraint stays pinned to it,
// precisely so this value can move without a migration while never letting an
// upload past what the column will physically hold.
public sealed class FarmLogoOptions
{
    public const string SectionName = "FarmLogo";

    // 2 MB by default. Room for a photographic logo without inviting a
    // multi-megabyte upload buffered whole in memory on every request.
    public int MaxUploadBytes { get; init; } = 2 * 1024 * 1024;
}

// Fail fast at startup rather than at the first upload: a misconfigured limit
// is a deployment error, and one set above the ceiling would hand every request
// a buffer larger than the column can store — the Large-Object-Heap pressure
// the ceiling exists to bound. ValidateOnStart turns it into a boot failure.
public sealed class FarmLogoOptionsValidator : IValidateOptions<FarmLogoOptions>
{
    public ValidateOptionsResult Validate(string? name, FarmLogoOptions options)
    {
        if (options.MaxUploadBytes <= 0)
            return ValidateOptionsResult.Fail(
                $"{FarmLogoOptions.SectionName}:{nameof(FarmLogoOptions.MaxUploadBytes)} must be greater than zero.");

        if (options.MaxUploadBytes > ImageSanitizer.MaxByteLengthCeiling)
            return ValidateOptionsResult.Fail(
                $"{FarmLogoOptions.SectionName}:{nameof(FarmLogoOptions.MaxUploadBytes)} " +
                $"({options.MaxUploadBytes}) cannot exceed the {ImageSanitizer.MaxByteLengthCeiling}-byte ceiling.");

        return ValidateOptionsResult.Success;
    }
}
