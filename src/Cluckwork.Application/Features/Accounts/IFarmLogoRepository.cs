namespace Cluckwork.Application.Features.Accounts;

using Cluckwork.Domain.Accounts;

// The logo bytes are the largest thing this application stores, so the port is
// split by how much of the row each caller actually needs (#123).
public interface IFarmLogoRepository
{
    // Projection WITHOUT the bytes. The serve endpoint asks for this first so
    // an If-None-Match hit can answer 304 without ever reading a megabyte out
    // of the database, and the account read uses it to say "there is a logo,
    // here is its version" for the same price.
    Task<FarmLogoMetadata?> GetMetadataAsync(CancellationToken ct = default);

    // Bytes included — only the serve endpoint's cache-miss path.
    Task<FarmLogoContent?> GetContentAsync(CancellationToken ct = default);

    // Tracked, for replace and delete.
    Task<FarmLogo?> GetTrackedAsync(CancellationToken ct = default);

    void Add(FarmLogo logo);

    void Remove(FarmLogo logo);
}

public sealed record FarmLogoMetadata(
    string ContentType, string ContentHash, int Width, int Height, int ByteLength, DateTimeOffset UpdatedAt);

public sealed record FarmLogoContent(
    byte[] Content, string ContentType, string ContentHash, DateTimeOffset UpdatedAt);
