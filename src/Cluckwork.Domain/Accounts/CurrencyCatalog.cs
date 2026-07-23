namespace Cluckwork.Domain.Accounts;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;

// Spec §4.6 "Currency derivation fallback": Phase 1 ships a static ISO 4217
// lookup. Symbol and minor unit come from deliberately DIFFERENT sources:
//
//   symbol      — from the framework's own CLDR/ICU data. It is a display
//                 concern, ICU is the authority on it, and hand-listing ~180
//                 symbols would rot.
//   minor unit  — from the short ISO 4217 exception list below. It is NOT a
//                 display concern: it decides how a stored integer amount is
//                 read back (1234 = $12.34 or ¥1234), so it must follow the
//                 standard, not a locale's formatting habit. ICU's
//                 CurrencyDecimalDigits is a display convention and diverges
//                 for some currencies (cash rounding), which would silently
//                 misread stored money.
//
// Unknown code → symbol = code, minor unit = 2 (§4.6).
public static class CurrencyCatalog
{
    public const int DefaultMinorUnit = 2;

    // ISO 4217 currencies whose minor unit is not 2. Everything else is 2.
    private static readonly FrozenDictionary<string, int> MinorUnitExceptions =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIF"] = 0, ["CLP"] = 0, ["DJF"] = 0, ["GNF"] = 0, ["ISK"] = 0,
            ["JPY"] = 0, ["KMF"] = 0, ["KRW"] = 0, ["PYG"] = 0, ["RWF"] = 0,
            ["UGX"] = 0, ["UYI"] = 0, ["VND"] = 0, ["VUV"] = 0, ["XAF"] = 0,
            ["XOF"] = 0, ["XPF"] = 0,
            ["BHD"] = 3, ["IQD"] = 3, ["JOD"] = 3, ["KWD"] = 3, ["LYD"] = 3,
            ["OMR"] = 3, ["TND"] = 3,
            ["CLF"] = 4, ["UYW"] = 4
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> Symbols = BuildSymbols();

    // A well-formed code is what the catalog can be asked about at all: three
    // ASCII letters (ISO 4217). Format only — an unlisted code is legal and
    // takes the fallback, per §4.6.
    public static bool IsWellFormedCode(string? currencyCode) =>
        currencyCode is { Length: 3 } code && code.All(char.IsAsciiLetter);

    public static CurrencyInfo Resolve(string? currencyCode)
    {
        var code = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        return new CurrencyInfo(
            code,
            Symbols.TryGetValue(code, out var symbol) ? symbol : code,
            MinorUnitExceptions.TryGetValue(code, out var minorUnit)
                ? minorUnit
                : DefaultMinorUnit);
    }

    private static FrozenDictionary<string, string> BuildSymbols()
    {
        // culture → its region's ISO currency code + that culture's symbol.
        var best = new Dictionary<string, (string Symbol, string CultureName)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo region;
            try
            {
                region = new RegionInfo(culture.Name);
            }
            catch (ArgumentException)
            {
                continue; // culture with no region of its own
            }

            var code = region.ISOCurrencySymbol;
            if (!IsWellFormedCode(code)) continue;

            var symbol = Canonicalize(culture.NumberFormat.CurrencySymbol);
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            // Cultures sharing a currency can disagree on the symbol ("$" vs
            // "US$"). Resolve it deterministically — shortest symbol wins, ties
            // by culture name — so a farm does not see a different symbol than
            // the one it was shown when it picked the currency.
            if (!best.TryGetValue(code, out var current)
                || symbol.Length < current.Symbol.Length
                || (symbol.Length == current.Symbol.Length
                    && string.CompareOrdinal(culture.Name, current.CultureName) < 0))
            {
                best[code] = (symbol, culture.Name);
            }
        }

        return best.ToFrozenDictionary(
            kv => kv.Key, kv => kv.Value.Symbol, StringComparer.OrdinalIgnoreCase);
    }

    // ICU versions disagree on the WIDTH of some currency symbols: one build
    // gives ja-JP the halfwidth yen sign (U+00A5 ¥), the next the fullwidth one
    // (U+FFE5 ￥). They are the same character to a reader and the same length
    // to a tiebreak, so nothing above can choose between them — the table would
    // quietly differ between a dev machine and the server. Compatibility
    // normalization is the framework's own answer: it folds every fullwidth
    // form onto its canonical one.
    private static string Canonicalize(string symbol) =>
        symbol.IsNormalized(NormalizationForm.FormKC)
            ? symbol
            : symbol.Normalize(NormalizationForm.FormKC);
}

public sealed record CurrencyInfo(string Code, string Symbol, int MinorUnit);
