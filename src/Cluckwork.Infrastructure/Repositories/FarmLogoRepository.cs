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
    public Task<FarmLogoMetadata?> GetMetadataAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Select(l => new FarmLogoMetadata(
                l.ContentType, l.ContentHash, l.Width, l.Height, l.ByteLength, l.UpdatedAt))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogoContent?> GetContentAsync(CancellationToken ct = default) =>
        db.FarmLogos
            .AsNoTracking()
            .Select(l => new FarmLogoContent(l.Content, l.ContentType, l.ContentHash, l.UpdatedAt))
            .FirstOrDefaultAsync(ct);

    public Task<FarmLogo?> GetTrackedAsync(CancellationToken ct = default) =>
        db.FarmLogos.FirstOrDefaultAsync(ct);

    public void Add(FarmLogo logo) => db.FarmLogos.Add(logo);

    public void Remove(FarmLogo logo) => db.FarmLogos.Remove(logo);
}
