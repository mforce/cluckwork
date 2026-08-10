namespace Cluckwork.Domain.Accounts;

using System.Security.Cryptography;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Media;

// The farm's logo AND banner (#123, #179). Spec §3.2 has no logo field — this
// slice adds one, and the spec is updated alongside.
//
// Its own table, deliberately, rather than a column on Account. Account is on
// the hot path: every sales order, expense, inventory purchase and product
// write calls GetCurrentAsync, and EF's generated SELECT lists every mapped
// column, so a megabyte of bytea on that row would be detoasted out of TOAST
// and put on the wire for all of them. Nothing but the logo/banner endpoints
// ever touches this table.
//
// One row per farm, enforced by a unique index on FarmId rather than by making
// the account the primary key — when a real farms aggregate replaces
// SeedDefaults.FarmId, the constraint already says what it means.
//
// NAME DISCREPANCY, KEPT ON PURPOSE (#179): this class and table are still
// called FarmLogo/FarmLogos even though a row can also hold a banner — a
// second, independent wide/hero image the SPA shows on a post-login splash,
// distinct from the square sidebar mark. Renaming to something like
// FarmBranding was considered and rejected: it would touch every layer of a
// shipped, tested feature (#123) — repository, handlers, endpoints, options,
// audit action, migrations with live data — to fix a name, for zero functional
// gain. The two images share this row instead of getting a table each because
// a farm has at most one of each, and a second near-identical table (own
// migration, own EF configuration, own repository) was judged not worth it for
// one extra image. The logo and banner sides are otherwise fully independent —
// see HasLogo/HasBanner and Replace/ReplaceBanner/ClearLogo/ClearBanner below.
public sealed class FarmLogo : AggregateRoot<Guid>
{
    public Guid FarmId { get; private set; }

    // --- Logo (square sidebar mark) ---
    // All null until the first upload, and after a removal — a row can hold a
    // banner with no logo, or vice versa, so neither side can require the
    // other's presence.

    // The SANITIZED bytes, not what the client sent. See ImageSanitizer.
    public byte[]? Content { get; private set; }

    // Sniffed from the content, never the client's declared type.
    public string? ContentType { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    // Stored rather than derived from Content. `l.Content.Length` does
    // translate — Npgsql emits `length("Content")` — but bytea is EXTENDED
    // storage, so length() on a compressed out-of-line value has to fetch and
    // decompress it. That would put the megabyte back into exactly the
    // metadata-only reads that exist to avoid it (review of #168).
    public int? ByteLength { get; private set; }

    // Hex SHA-256 of Content. Derived here rather than at the endpoint so it
    // cannot drift from the bytes it describes: the two only ever change
    // together, inside this class. The serve endpoint quotes it into an HTTP
    // ETag, which makes a replaced logo self-invalidating and lets an unchanged
    // one come back as a 304.
    public string? ContentHash { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool HasLogo => Content is not null;

    // --- Banner (post-login splash, #179) ---
    // Same shape as the logo fields above, deliberately duplicated rather than
    // shared: the two images have independent presence, independent size
    // policy (FarmBannerOptions vs FarmLogoOptions), and independent error
    // codes out of ImageSanitizer (FarmBanner.* vs FarmLogo.*).

    public byte[]? BannerContent { get; private set; }

    public string? BannerContentType { get; private set; }

    public int? BannerWidth { get; private set; }

    public int? BannerHeight { get; private set; }

    public int? BannerByteLength { get; private set; }

    public string? BannerContentHash { get; private set; }

    public DateTimeOffset? BannerUpdatedAt { get; private set; }

    public bool HasBanner => BannerContent is not null;

    // Optimistic concurrency, like every other mutable aggregate here.
    //
    // ONE token shared by both the logo and banner sides (#179) — an accepted
    // tradeoff, not an oversight. A logo replace and a banner replace landing
    // close together can 409 each other even though they touch disjoint
    // columns, because EF's WHERE clause is `Version = <snapshot>` for the
    // whole row. Judged acceptable: both are rare, deliberate admin actions,
    // not concurrent by nature, and the alternative (a table per asset) was
    // rejected above.
    //
    // The previous slice argued a logo needed none — worst case last-write-wins,
    // and `Replace` rewrites every field together so the row stays coherent.
    // That was wrong, and the way it was wrong is worth keeping: EF updates only
    // the properties that differ from EACH CONTEXT'S OWN snapshot. Two writers
    // replacing the same logo, one with a 32x32 JPEG and one with a different
    // 1x1 PNG of the same byte length, produce an UPDATE from the second that
    // touches Content and ContentHash but not type, dimensions or length —
    // because relative to ITS snapshot those did not change. The row ends up
    // holding PNG bytes labelled image/jpeg at 32x32 (codex round 2 of #168).
    //
    // No client sends this: a raw-body PUT has no base version to carry. It is
    // server-side only, and the loser gets the 409 that Program.cs already maps
    // from DbUpdateConcurrencyException.
    public int Version { get; private set; }

    private FarmLogo() { }

    // A bare row, holding neither image yet — the caller follows up with
    // Replace and/or ReplaceBanner. Kept as a single unparameterized factory
    // (rather than one overload per asset) because a row's identity (Id,
    // AccountId, FarmId) has nothing to do with which image it ends up holding.
    public static FarmLogo Create(Guid id, Guid accountId, Guid farmId) =>
        new() { Id = id, AccountId = accountId, FarmId = farmId };

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
        Version++;
    }

    public void ReplaceBanner(SanitizedImage image, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);

        BannerContent = image.Content;
        BannerContentType = image.ContentType;
        BannerWidth = image.Width;
        BannerHeight = image.Height;
        BannerByteLength = image.Content.Length;
        BannerContentHash = Convert.ToHexString(SHA256.HashData(image.Content)).ToLowerInvariant();
        BannerUpdatedAt = now;
        Version++;
    }

    public void ClearLogo()
    {
        Content = null;
        ContentType = null;
        Width = null;
        Height = null;
        ByteLength = null;
        ContentHash = null;
        UpdatedAt = null;
        Version++;
    }

    public void ClearBanner()
    {
        BannerContent = null;
        BannerContentType = null;
        BannerWidth = null;
        BannerHeight = null;
        BannerByteLength = null;
        BannerContentHash = null;
        BannerUpdatedAt = null;
        Version++;
    }
}
