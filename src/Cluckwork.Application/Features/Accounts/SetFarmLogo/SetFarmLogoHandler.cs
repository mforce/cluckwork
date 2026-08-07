namespace Cluckwork.Application.Features.Accounts.SetFarmLogo;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;

// #123 — upload or replace the farm logo.
//
// The uploaded bytes are never stored. ImageSanitizer walks the container and
// hands back a rewritten copy; that copy is what reaches the database, and
// everything the row records about it (content type, dimensions, hash) is read
// off the rewrite rather than off anything the client claimed.
public sealed class SetFarmLogoHandler(
    IFarmLogoRepository logos,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IClock clock)
{
    // `maxByteLength` is the OPERATIONAL cap, supplied by the endpoint from
    // config (#123). The Application layer takes it as a plain int rather than
    // reading the API's options type — the sanitizer is the authority on
    // "too large", and this is the size it judges against.
    public async Task<Result<FarmLogoMetadata>> HandleAsync(
        ReadOnlyMemory<byte> upload, Guid accountId, int maxByteLength, CancellationToken ct)
    {
        var sanitized = ImageSanitizer.Sanitize(upload.Span, maxByteLength);
        if (sanitized.IsFailure) return Result.Failure<FarmLogoMetadata>(sanitized.Error);

        var image = sanitized.Value;
        var now = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);

        var existing = await logos.GetTrackedAsync(ct);
        if (existing is null)
        {
            existing = FarmLogo.Create(
                Guid.NewGuid(), accountId, SeedDefaults.FarmId, image, now);
            logos.Add(existing);
        }
        else
        {
            existing.Replace(image, now);
        }

        // Details describe the image, never carry it — an audit row with a
        // megabyte of base64 in its JSON would make the audit viewer unusable
        // and the table enormous.
        await audit.WriteAsync(
            AuditActions.AccountSetLogo, nameof(FarmLogo), existing.Id,
            reason: null,
            details: new
            {
                contentType = image.ContentType,
                width = image.Width,
                height = image.Height,
                storedBytes = image.Content.Length,
                uploadedBytes = upload.Length,
                contentHash = existing.ContentHash
            },
            ct: ct);

        await unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new FarmLogoMetadata(
            existing.ContentType, existing.ContentHash,
            existing.Width, existing.Height, existing.Content.Length, existing.UpdatedAt));
    }
}
