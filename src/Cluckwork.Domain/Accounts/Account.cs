namespace Cluckwork.Domain.Accounts;

// The farm's own settings row. Spec §3.2 models `farms` as a table under the
// account; there is no farms aggregate yet (SeedDefaults.FarmId is a stand-in),
// so for the single-farm Phase 1 the §4.5 localization fields live here on the
// account — the same row IFarmClock already reads TimeZoneId from (#35) and the
// same currency financial rows already snapshot at creation (§4.6).
public sealed class Account : AggregateRoot<Guid>
{
    public const int MaxNameLength = 120;
    public const int MaxLocaleLength = 32;
    public const int MaxTimeZoneIdLength = 64;
    public const int MaxFormatOverrideLength = 32;
    public const string DefaultLocale = "en-US";

    public string Name { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = "UTC";
    public string Locale { get; private set; } = DefaultLocale;
    public string DefaultCurrencyCode { get; private set; } = "USD";
    public string? DefaultCurrencySymbol { get; private set; }
    public int DefaultCurrencyMinorUnit { get; private set; } = CurrencyCatalog.DefaultMinorUnit;

    // Rows created before #123 have no stored symbol. It is derived data, not
    // authored data, so resolve it through the same §4.6 catalog rather than
    // backfilling — and never fall back for the MINOR UNIT, which is a stored
    // contract about how existing amounts are read.
    public string CurrencySymbol =>
        DefaultCurrencySymbol ?? CurrencyCatalog.Resolve(DefaultCurrencyCode).Symbol;

    public UnitSystem UnitSystem { get; private set; } = UnitSystem.Metric;
    // Null = follow the locale's own convention.
    public DayOfWeek? FirstDayOfWeek { get; private set; }
    public string? DateFormatOverride { get; private set; }
    public string? TimeFormatOverride { get; private set; }
    public bool IsActive { get; private set; }
    public int Version { get; private set; }

    private Account() { }

    public static Account Create(
        Guid id, string name, string timeZoneId, string currencyCode,
        string locale = DefaultLocale)
    {
        var currency = CurrencyCatalog.Resolve(currencyCode);
        return new Account
        {
            Id = id,
            AccountId = id,
            Name = name,
            TimeZoneId = timeZoneId,
            Locale = locale,
            DefaultCurrencyCode = currency.Code,
            DefaultCurrencySymbol = currency.Symbol,
            DefaultCurrencyMinorUnit = currency.MinorUnit,
            IsActive = true
        };
    }

    // #123 — the whole settings block replaced under the Version token.
    //
    // `financialRowsExist` is passed in rather than probed: §4.6's currency
    // lock is a question about sales orders, payments and expenses, which this
    // aggregate cannot see. The caller answers it; the rule lives here.
    public Result UpdateSettings(
        string name,
        string timeZoneId,
        string locale,
        string currencyCode,
        UnitSystem unitSystem,
        DayOfWeek? firstDayOfWeek,
        string? dateFormatOverride,
        string? timeFormatOverride,
        bool financialRowsExist)
    {
        var guard = ValidateRequiredFields(name, timeZoneId, locale, currencyCode);
        if (guard.IsFailure) return guard;

        var normalizedCurrency = currencyCode.Trim().ToUpperInvariant();
        var currencyChanged = !string.Equals(
            normalizedCurrency, DefaultCurrencyCode, StringComparison.OrdinalIgnoreCase);

        if (currencyChanged && financialRowsExist)
            return Result.Failure(Error.Conflict(
                "Account.CurrencyLocked",
                "The farm currency cannot be changed once sales orders, payments or expenses exist. " +
                "Those rows keep the currency they were recorded in, and re-denominating them would " +
                "misstate history."));

        Name = name.Trim();
        TimeZoneId = timeZoneId.Trim();
        Locale = locale.Trim();
        UnitSystem = unitSystem;
        FirstDayOfWeek = firstDayOfWeek;
        DateFormatOverride = Normalize(dateFormatOverride);
        TimeFormatOverride = Normalize(timeFormatOverride);

        // Only re-derive on an actual change (§4.6). Refreshing the symbol and
        // minor unit on every save would let a catalog update silently
        // reinterpret money already stored under the old minor unit.
        if (currencyChanged)
        {
            var currency = CurrencyCatalog.Resolve(normalizedCurrency);
            DefaultCurrencyCode = currency.Code;
            DefaultCurrencySymbol = currency.Symbol;
            DefaultCurrencyMinorUnit = currency.MinorUnit;
        }

        Version++;
        return Result.Success();
    }

    // Spec §4.5: an active farm is not operable without a timezone, a locale
    // and a currency — every date rule, every format and every money row reads
    // one of them.
    private static Result ValidateRequiredFields(
        string name, string timeZoneId, string locale, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation(
                "Account.NameRequired", "A farm name is required."));
        if (name.Trim().Length > MaxNameLength)
            return Result.Failure(Error.Validation(
                "Account.NameTooLong", $"Farm name cannot exceed {MaxNameLength} characters."));
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return Result.Failure(Error.Validation(
                "Account.TimeZoneRequired", "A timezone is required."));
        if (string.IsNullOrWhiteSpace(locale))
            return Result.Failure(Error.Validation(
                "Account.LocaleRequired", "A locale is required."));
        if (!CurrencyCatalog.IsWellFormedCode(currencyCode?.Trim()))
            return Result.Failure(Error.Validation(
                "Account.CurrencyCodeInvalid",
                "A currency code is required and must be a three-letter ISO 4217 code."));
        return Result.Success();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
