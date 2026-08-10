namespace Cluckwork.Application.Features.Accounts;

using Cluckwork.Domain.Accounts;

// The logo/banner bytes are the largest thing this application stores, so the
// port is split by how much of the row each caller actually needs (#123), and
// by which of the two independent images (logo vs banner, #179) it wants — a
// caller asking for one is never handed the other's presence or bytes.
public interface IFarmLogoRepository
{
    // Projection WITHOUT the bytes. The serve endpoint asks for this first so
    // an If-None-Match hit can answer 304 without ever reading a megabyte out
    // of the database, and the account read uses it to say "there is a logo,
    // here is its version" for the same price. Null when the row doesn't exist
    // OR exists but holds no logo (a banner-only row).
    Task<FarmLogoMetadata?> GetLogoMetadataAsync(CancellationToken ct = default);

    // Bytes included — only the serve endpoint's cache-miss path.
    Task<FarmLogoContent?> GetLogoContentAsync(CancellationToken ct = default);

    Task<FarmLogoMetadata?> GetBannerMetadataAsync(CancellationToken ct = default);

    Task<FarmLogoContent?> GetBannerContentAsync(CancellationToken ct = default);

    // Both content hashes in one round trip — what /account and /account/settings
    // need (#179): "is there a logo/banner, and which version". Never null even
    // when the row itself doesn't exist, so callers don't need a second branch
    // beyond the two hash fields already being null.
    Task<FarmBrandingHashes> GetBrandingHashesAsync(CancellationToken ct = default);

    // Tracked, for replace and delete of either asset.
    Task<FarmLogo?> GetTrackedAsync(CancellationToken ct = default);

    void Add(FarmLogo logo);

    void Remove(FarmLogo logo);
}

public sealed record FarmLogoMetadata(
    string ContentType, string ContentHash, int Width, int Height, int ByteLength, DateTimeOffset UpdatedAt);

public sealed record FarmLogoContent(
    byte[] Content, string ContentType, string ContentHash, DateTimeOffset UpdatedAt);

public sealed record FarmBrandingHashes(string? LogoContentHash, string? BannerContentHash);
