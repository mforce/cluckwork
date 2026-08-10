namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// One row per tenant, so FirstOrDefault under the query filter is the farm's
// logo (#123).
public sealed class FarmLogoRepository(AppDbContext db) : IFarmLogoRepository
{
    // The projection is the point: Content is left out of the SELECT, so
    // Postgres never detoasts it and the bytes never cross the wire. Anything
    // that only needs to know "is there a logo, and which one" uses this.
    //
    // Filtered on Content != null rather than unconditionally FirstOrDefault:
    // the row (#179) can exist for a banner-only farm, and that is "no logo",
    // same as no row at all.
    public Task<FarmLogoMetadata?> GetLogoMetadataAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Where(l => l.Content != null)
            .Select(l => new FarmLogoMetadata(
                l.ContentType!, l.ContentHash!, l.Width!.Value, l.Height!.Value, l.ByteLength!.Value, l.UpdatedAt!.Value))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogoContent?> GetLogoContentAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Where(l => l.Content != null)
            .Select(l => new FarmLogoContent(l.Content!, l.ContentType!, l.ContentHash!, l.UpdatedAt!.Value))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogoMetadata?> GetBannerMetadataAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Where(l => l.BannerContent != null)
            .Select(l => new FarmLogoMetadata(
                l.BannerContentType!, l.BannerContentHash!, l.BannerWidth!.Value, l.BannerHeight!.Value,
                l.BannerByteLength!.Value, l.BannerUpdatedAt!.Value))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogoContent?> GetBannerContentAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Where(l => l.BannerContent != null)
            .Select(l => new FarmLogoContent(l.BannerContent!, l.BannerContentType!, l.BannerContentHash!, l.BannerUpdatedAt!.Value))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogo?> GetTrackedAsync(CancellationToken ct = default) =>
        db.FarmLogos.FirstOrDefaultAsync(ct);

    // One query for both hashes (#179 review) rather than the two
    // GetLogoMetadataAsync/GetBannerMetadataAsync calls /account and
    // /account/settings used before — same "no bytes, no unneeded columns"
    // shape, just one row read instead of two. The conditional null (rather
    // than filtering by Where, as the two calls above do) is what makes a
    // single row answer both questions in one round trip: a Where clause can
    // only ever say yes/no to ONE presence check per query.
    public async Task<FarmBrandingHashes> GetBrandingHashesAsync(CancellationToken ct = default) =>
        await db.FarmLogos
            .AsNoTracking()
            .Select(l => new FarmBrandingHashes(
                l.Content != null ? l.ContentHash : null,
                l.BannerContent != null ? l.BannerContentHash : null))
            .FirstOrDefaultAsync(ct)
        ?? new FarmBrandingHashes(null, null);

    public void Add(FarmLogo logo) => db.FarmLogos.Add(logo);

    public void Remove(FarmLogo logo) => db.FarmLogos.Remove(logo);
}
