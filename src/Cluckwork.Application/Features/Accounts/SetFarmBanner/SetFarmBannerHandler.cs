namespace Cluckwork.Application.Features.Accounts.SetFarmBanner;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;

// #179 — upload or replace the farm banner. Mirrors SetFarmLogoHandler exactly;
// the only difference is which side of the shared FarmLogo row it writes and
// which ImageAssetKind it sanitizes against (see ImageSanitizer.ImageAssetKind).
public sealed class SetFarmBannerHandler(
    IFarmLogoRepository logos,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IClock clock)
{
    public async Task<Result<FarmLogoMetadata>> HandleAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct)
    {
        var sanitized = ImageSanitizer.Sanitize(
            upload.Span, maxByteLength, ImageSanitizer.ImageAssetKind.Banner);
        if (sanitized.IsFailure) return Result.Failure<FarmLogoMetadata>(sanitized.Error);

        var image = sanitized.Value;
        var now = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);

        var existing = await logos.GetTrackedAsync(ct);
        if (existing is null)
        {
            existing = FarmLogo.Create(Guid.NewGuid(), accountId, SeedDefaults.FarmId);
            logos.Add(existing);
        }

        existing.ReplaceBanner(image, now);

        await audit.WriteAsync(
            AuditActions.AccountSetBanner, nameof(FarmLogo), existing.Id,
            reason: null,
            details: new
            {
                contentType = image.ContentType,
                width = image.Width,
                height = image.Height,
                storedBytes = image.Content.Length,
                uploadedBytes = upload.Length,
                contentHash = existing.BannerContentHash
            },
            ct: ct);

        await unitOfWork.SaveChangesAsync(ct);

        // Non-null: ReplaceBanner() just set every one of these on this same instance.
        return Result.Success(new FarmLogoMetadata(
            existing.BannerContentType!, existing.BannerContentHash!,
            existing.BannerWidth!.Value, existing.BannerHeight!.Value,
            existing.BannerContent!.Length, existing.BannerUpdatedAt!.Value));
    }
}
