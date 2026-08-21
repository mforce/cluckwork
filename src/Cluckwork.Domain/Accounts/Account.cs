namespace Cluckwork.Domain.Accounts;

using System.Text.RegularExpressions;
using Cluckwork.Domain.Catalog;

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
    public const int SlugMaxLength = 32;

    // Farm code (#531). Lowercase, URL-safe, stored ALREADY-NORMALIZED so a
    // plain unique index suffices — deliberately NOT a lower("Slug") expression
    // index (the four in InitialCreate are un-regenerable #407 fixtures; no
    // reason to mint a fifth). Immutable this epic (decision 10): there is no
    // ChangeSlug, on purpose — a provisioning typo has no in-epic fix, which is
    // why #533's provision-account echoes the slug before it commits.
    public static readonly IReadOnlySet<string> ReservedSlugs =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "api", "admin", "www", "health", "app", "static", "assets", "login", "auth",
        };

    // 3–32 chars, lowercase alnum + hyphen, no leading/trailing hyphen.
    // UPPERCASE IS REJECTED, not folded: the stored value is guaranteed
    // lowercase, which is exactly what lets the unique index be plain.
    private static readonly Regex SlugPattern =
        new("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.Compiled);

    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
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
    // #444 — the pack unit Daily Entry's steppers bump by (e.g. Tray = +30/-30)
    // when a user hasn't set their own override (ApplicationUser.PreferredStepperUnit).
    // Individual keeps today's +1/-1 behavior unchanged for a farm that never sets this.
    public EggUnit DefaultStepperUnit { get; private set; } = EggUnit.Individual;
    // Null = follow the locale's own convention.
    public DayOfWeek? FirstDayOfWeek { get; private set; }
    public string? DateFormatOverride { get; private set; }
    public string? TimeFormatOverride { get; private set; }

    // The farm's accent palette (#149). Farm-wide and admin-chosen, orthogonal
    // to each user's own light/night preference, which the SPA keeps locally.
    public string Brand { get; private set; } = FarmBrands.Default;
    public bool IsActive { get; private set; }
    public int Version { get; private set; }

    private Account() { }

    public static Account Create(
        Guid id, string name, string slug, string timeZoneId, string currencyCode,
        string locale = DefaultLocale)
    {
        var normalizedSlug = ValidateSlug(slug);
        var currency = CurrencyCatalog.Resolve(currencyCode);
        return new Account
        {
            Id = id,
            AccountId = id,
            Name = name.Trim(),
            Slug = normalizedSlug,
            TimeZoneId = timeZoneId.Trim(),
            Locale = locale,
            DefaultCurrencyCode = currency.Code,
            DefaultCurrencySymbol = currency.Symbol,
            DefaultCurrencyMinorUnit = currency.MinorUnit,
            IsActive = true
        };
    }

    public static Result<string> TryValidateSlug(string? slug)
    {
        var normalized = (slug ?? string.Empty).Trim();
        if (!SlugPattern.IsMatch(normalized))
            return Result.Failure<string>(Error.Validation(
                "Account.SlugInvalid",
                $"'{slug}' is not a valid farm code (lowercase letters, digits and hyphens, " +
                "3–32 characters, no leading or trailing hyphen)."));
        if (ReservedSlugs.Contains(normalized))
            return Result.Failure<string>(Error.Validation(
                "Account.SlugInvalid", $"'{normalized}' is a reserved farm code."));
        return Result.Success(normalized);
    }

    // Invariant guard (throws), consistent with Flock.Create. Provisioning uses
    // TryValidateSlug for an expected failure; every other factory caller keeps
    // this backstop. One regex and one reserved set own both paths.
    private static string ValidateSlug(string slug)
    {
        var result = TryValidateSlug(slug);
        if (result.IsFailure)
            throw new ArgumentException(result.Error.Description, nameof(slug));
        return result.Value;
    }

    // #531 — take the farm offline / bring it back. IsActive already existed and
    // nothing read it; enforcement (blocking a suspended account's login) is
    // #532. Each bumps Version — the EF concurrency token EF never auto-
    // increments — so two concurrent writers cannot both match WHERE Version=N;
    // the loser gets a DbUpdateConcurrencyException instead of silently
    // overwriting. Unconditional on purpose: no current-state guard, so a
    // suspend/reactivate always advances the token.
    public void Suspend()
    {
        IsActive = false;
        Version++;
    }

    public void Reactivate()
    {
        IsActive = true;
        Version++;
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
        string brand,
        EggUnit defaultStepperUnit,
        bool financialRowsExist)
    {
        var guard = ValidateRequiredFields(name, timeZoneId, locale, currencyCode);
        if (guard.IsFailure) return guard;

        // Curated set only (#149). Validated HERE rather than in
        // UpdateFarmSettingsValidator on purpose: a boundary rule would reject
        // first and return 400, while a domain failure reaches MapFailure's
        // fallback arm as 422 with title = the error code — the stable
        // machine-readable code the issue asks for, no new plumbing. Same route
        // Account.CurrencyLocked already takes.
        //
        // Canonicalized like the currency code above: CSS matches
        // data-brand="forest" exactly, so storing "Forest" would render the
        // default forever with nothing to show for it.
        var normalizedBrand = (brand ?? string.Empty).Trim().ToLowerInvariant();
        if (!FarmBrands.IsCurated(normalizedBrand))
            return Result.Failure(Error.Validation(
                "Account.UnknownBrand",
                $"'{brand}' is not one of the available farm palettes."));

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
        Brand = normalizedBrand;
        DefaultStepperUnit = defaultStepperUnit;

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
