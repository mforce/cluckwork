namespace Cluckwork.Domain.Accounts;

using System.Security.Cryptography;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;

// The farm's logo (#123). Spec §3.2 has no logo field — this slice adds one,
// and the spec is updated alongside.
//
// Its own table, deliberately, rather than a column on Account. Account is on
// the hot path: every sales order, expense, inventory purchase and product
// write calls GetCurrentAsync, and EF's generated SELECT lists every mapped
// column, so a megabyte of bytea on that row would be detoasted out of TOAST
// and put on the wire for all of them. Nothing but the logo endpoints ever
// touches this table.
//
// One row per farm, enforced by a unique index on FarmId rather than by making
// the account the primary key — when a real farms aggregate replaces
// SeedDefaults.FarmId, the constraint already says what it means.
public sealed class FarmLogo : AggregateRoot<Guid>
{
    public Guid FarmId { get; private set; }

    // The SANITIZED bytes, not what the client sent. See ImageSanitizer.
    public byte[] Content { get; private set; } = [];

    // Sniffed from the content, never the client's declared type.
    public string ContentType { get; private set; } = string.Empty;

    public int Width { get; private set; }
    public int Height { get; private set; }

    // Stored rather than derived from Content. `l.Content.Length` does
    // translate — Npgsql emits `length("Content")` — but bytea is EXTENDED
    // storage, so length() on a compressed out-of-line value has to fetch and
    // decompress it. That would put the megabyte back into exactly the
    // metadata-only reads that exist to avoid it (review of #168).
    public int ByteLength { get; private set; }

    // Hex SHA-256 of Content. Derived here rather than at the endpoint so it
    // cannot drift from the bytes it describes: the two only ever change
    // together, inside this class. The serve endpoint quotes it into an HTTP
    // ETag, which makes a replaced logo self-invalidating and lets an unchanged
    // one come back as a 304.
    public string ContentHash { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    private FarmLogo() { }

    public static FarmLogo Create(
        Guid id, Guid accountId, Guid farmId, SanitizedImage image, DateTimeOffset now)
    {
        var logo = new FarmLogo { Id = id, AccountId = accountId, FarmId = farmId };
        logo.Replace(image, now);
        return logo;
    }

    public void Replace(SanitizedImage image, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);

        Content = image.Content;
        ContentType = image.ContentType;
        Width = image.Width;
        Height = image.Height;
        ByteLength = image.Content.Length;
        ContentHash = Convert.ToHexString(SHA256.HashData(image.Content)).ToLowerInvariant();
        UpdatedAt = now;
    }
}
