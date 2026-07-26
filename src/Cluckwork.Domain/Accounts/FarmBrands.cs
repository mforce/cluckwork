namespace Cluckwork.Domain.Accounts;

// The curated farm accent palettes (#149). Curated rather than free-form: an
// arbitrary colour cannot be held to the AA contrast bands the token system is
// tuned for, and every id here ships a hand-checked light and dark pair.
//
// Ids are lowercase because they are written straight into the DOM as
// `data-brand="<id>"` and matched by exact-match CSS selectors.
//
// MIRRORED by web/src/lib/brand.ts (BRANDS) and by the palette blocks in
// web/src/styles.css. All three are the same curated set expressed three ways;
// adding a palette means touching all three. A palette present here but missing
// from the CSS renders as the default rather than failing, and one present in
// the SPA but missing here is refused on save with Account.UnknownBrand — both
// loud enough to catch, but check the siblings when you change this.
public static class FarmBrands
{
    public const string Default = "aubergine";

    // Longest id ("terracotta") is 10; the column is sized with headroom.
    public const int MaxLength = 32;

    public static IReadOnlyList<string> All { get; } =
        [Default, "forest", "slate", "terracotta"];

    // Exact ordinal match against the curated set, deliberately not
    // Enum.TryParse: that accepts the underlying number ("0") and, for any enum,
    // a comma-separated list it ORs together — see UpdateFarmSettingsValidator's
    // note on the same trap.
    public static bool IsCurated(string brand) => All.Contains(brand, StringComparer.Ordinal);
}
