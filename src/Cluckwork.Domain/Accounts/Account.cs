namespace Cluckwork.Domain.Accounts;

using System.Text.RegularExpressions;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Sales;

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
    // A farm with no zone stated at provisioning time starts here and its Owner
    // sets the real one in Settings (#264). Named so the CLI default and the
    // property initialiser below cannot drift apart.
    public const string DefaultTimeZoneId = "UTC";
    public const int SlugMaxLength = 32;

    // Farm code (#531). Lowercase, URL-safe, stored ALREADY-NORMALIZED so a
    // plain unique index suffices — deliberately NOT a lower("Slug") expression
    // index (the four in InitialCreate are un-regenerable #407 fixtures; no
    // reason to mint a fifth). Renameable since #732 by the `rename-account`
    // verb and nothing else — there is deliberately no endpoint or Settings
    // field, and no retired-code list: a code a farm has moved off is
    // immediately reusable, which docs/decisions/732-farm-code-rename.md
    // records as the accepted cost.
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
    public string TimeZoneId { get; private set; } = DefaultTimeZoneId;
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

    // #612 — default for existing and new farms; AllFarmFlocks is an explicit
    // Owner/Manager opt-in. Only a plain Worker is ever affected by this.
    public WorkerSaleAllocationPolicy WorkerSaleAllocationPolicy { get; private set; } =
        WorkerSaleAllocationPolicy.AssignedFlocksOnly;

    // #727 — the most a ceiling-bound seller may take off a line's list price.
    // Plain nullable with no default and no backfill, unlike
    // WorkerSaleAllocationPolicy above: "no policy" was never a legal state,
    // but "no ceiling" IS the legal default here, so NULL says it directly and
    // Account.Create needs no ceiling argument. ZERO is a different, equally
    // legal setting meaning "give nothing away".
    public int? MaxDiscountBasisPoints { get; private set; }

    // Derived from the stored basis points — not a column, exactly like
    // CurrencySymbol above.
    public DiscountCeiling? MaxDiscount =>
        MaxDiscountBasisPoints is { } basisPoints
            ? DiscountCeiling.FromBasisPoints(basisPoints)
            : null;

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

    // #732 — the farm code is no longer immutable. Returns Result rather than throwing
    // because an invalid or reserved code is an EXPECTED failure on a path an operator
    // drives by hand; Create keeps the throwing ValidateSlug backstop for every other
    // factory caller. Validation is TryValidateSlug, the same single rule provisioning
    // uses: one regex and one reserved set own both paths.
    //
    // The same-code case returns success WITHOUT touching Version, and that is not
    // cosmetic. Version is the token UpdateFarmSettingsHandler compares a Farm Settings
    // save against, so a command that changed nothing must not advance it — the same
    // reasoning as the stateChanged gate in AccountSuspensionService. "Every aggregate
    // mutation bumps Version" stays true because on a no-op there is no mutation.
    public Result Rename(string? newSlug)
    {
        var validated = TryValidateSlug(newSlug);
        if (validated.IsFailure)
            return Result.Failure(validated.Error);

        if (string.Equals(validated.Value, Slug, StringComparison.Ordinal))
            return Result.Success();

        Slug = validated.Value;
        Version++;
        return Result.Success();
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
        WorkerSaleAllocationPolicy workerSaleAllocationPolicy,
        int? maxDiscountBasisPoints,
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

        // #727 — the backstop for the direct callers that never see
        // UpdateFarmSettingsValidator (#394). Validation, not a throw: over
        // HTTP the validator has already refused it, so reaching this means a
        // caller supplied a value the wire form cannot express.
        if (maxDiscountBasisPoints is { } basisPoints
            && basisPoints is < 0 or > DiscountCeiling.MaxBasisPoints)
            return Result.Failure(Error.Validation(
                "Account.MaxDiscountInvalid",
                $"A maximum discount must be between 0 and {DiscountCeiling.MaxBasisPoints} " +
                "basis points (0–100%)."));

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
        WorkerSaleAllocationPolicy = workerSaleAllocationPolicy;
        MaxDiscountBasisPoints = maxDiscountBasisPoints;

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
