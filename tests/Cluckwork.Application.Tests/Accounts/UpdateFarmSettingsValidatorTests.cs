namespace Cluckwork.Application.Tests.Accounts;

using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Domain.Accounts;

// #123 — the write-side gate on the §4.5 fields. The timezone rule carries the
// most weight: #35's FarmClock deliberately fails closed on an unusable zone
// rather than guessing a date, and this validator is what keeps an unusable
// zone from ever reaching the column.
public sealed class UpdateFarmSettingsValidatorTests
{
    private readonly UpdateFarmSettingsValidator _validator = new();

    private static UpdateFarmSettingsCommand Valid() => new(
        Name: "Sunrise Layers",
        TimeZoneId: "America/Los_Angeles",
        Locale: "en-US",
        CurrencyCode: "USD",
        UnitSystem: "Metric",
        FirstDayOfWeek: "Monday",
        DateFormatOverride: null,
        TimeFormatOverride: null,
        Brand: FarmBrands.Default,
        DefaultStepperUnit: "Individual",
        WorkerSaleAllocationPolicy: "AssignedFlocksOnly",
        Version: 0);

    private bool Fails(UpdateFarmSettingsCommand command, string property) =>
        _validator.Validate(command).Errors.Any(e => e.PropertyName == property);

    [Fact]
    public void ValidCommand_Passes() =>
        Assert.True(_validator.Validate(Valid()).IsValid);

    // --- timezone ---------------------------------------------------------

    [Theory]
    [InlineData("America/Los_Angeles")]
    [InlineData("Pacific/Auckland")]
    [InlineData("UTC")]
    public void KnownTimeZone_Passes(string tz) =>
        Assert.True(_validator.Validate(Valid() with { TimeZoneId = tz }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("America/Los Angeles")]   // space, not underscore
    [InlineData("PST")]                   // abbreviation, not an IANA id
    [InlineData("Pacific Standard Time")] // Windows id, not an IANA id
    [InlineData("posixrules")]            // host tzdata file, not a portable IANA id
    [InlineData("posix/America/Los_Angeles")]
    [InlineData("right/America/Los_Angeles")]
    public void UnusableTimeZone_Fails(string tz) =>
        Assert.True(Fails(Valid() with { TimeZoneId = tz }, nameof(UpdateFarmSettingsCommand.TimeZoneId)));

    // --- locale -----------------------------------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-MX")]
    [InlineData("ja-JP")]
    public void SpecificLocale_Passes(string locale) =>
        Assert.True(_validator.Validate(Valid() with { Locale = locale }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("en")]          // neutral: no region, so no number/date conventions
    [InlineData("zz-ZZ")]       // well-shaped but not a real culture
    [InlineData("not a locale")]
    public void UnusableLocale_Fails(string locale) =>
        Assert.True(Fails(Valid() with { Locale = locale }, nameof(UpdateFarmSettingsCommand.Locale)));

    // --- currency ---------------------------------------------------------

    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    [InlineData("ZZZ")]   // unlisted but well-formed — §4.6 fallback, not a 400
    public void WellFormedCurrencyCode_Passes(string code) =>
        Assert.True(_validator.Validate(Valid() with { CurrencyCode = code }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    public void MalformedCurrencyCode_Fails(string code) =>
        Assert.True(Fails(Valid() with { CurrencyCode = code }, nameof(UpdateFarmSettingsCommand.CurrencyCode)));

    // --- unit system / first day ------------------------------------------

    [Theory]
    [InlineData("Metric")]
    [InlineData("imperial")]
    public void KnownUnitSystem_Passes(string unitSystem) =>
        Assert.True(_validator.Validate(Valid() with { UnitSystem = unitSystem }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("Nautical")]
    [InlineData("0")]   // numeric enum values are not the wire contract
    public void UnknownUnitSystem_Fails(string unitSystem) =>
        Assert.True(Fails(Valid() with { UnitSystem = unitSystem }, nameof(UpdateFarmSettingsCommand.UnitSystem)));

    [Theory]
    [InlineData("Monday")]
    [InlineData("sunday")]
    [InlineData(null)]   // follow the locale
    [InlineData("")]
    public void ValidFirstDayOfWeek_Passes(string? day) =>
        Assert.True(_validator.Validate(Valid() with { FirstDayOfWeek = day }).IsValid);

    [Fact]
    public void UnknownFirstDayOfWeek_Fails() =>
        Assert.True(Fails(Valid() with { FirstDayOfWeek = "Funday" },
            nameof(UpdateFarmSettingsCommand.FirstDayOfWeek)));

    // Enum.TryParse ORs comma-separated values together for ANY enum, flags or
    // not, and the result is usually a defined member — so "Monday,Tuesday"
    // parsed to Wednesday and stored it, and "Metric,Imperial" stored Imperial
    // (adversarial review of #159).
    [Fact]
    public void CommaListedDays_Fail_RatherThanStoringTheirSum() =>
        Assert.True(Fails(Valid() with { FirstDayOfWeek = "Monday,Tuesday" },
            nameof(UpdateFarmSettingsCommand.FirstDayOfWeek)));

    [Fact]
    public void CommaListedUnitSystems_Fail() =>
        Assert.True(Fails(Valid() with { UnitSystem = "Metric,Imperial" },
            nameof(UpdateFarmSettingsCommand.UnitSystem)));

    // --- default stepper unit (#444) --------------------------------------

    [Theory]
    [InlineData("Individual")]
    [InlineData("tray")]
    [InlineData("Case")]
    public void KnownEggUnit_Passes(string unit) =>
        Assert.True(_validator.Validate(Valid() with { DefaultStepperUnit = unit }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("Bushel")]
    [InlineData("0")]   // numeric enum values are not the wire contract
    public void UnknownEggUnit_Fails(string unit) =>
        Assert.True(Fails(Valid() with { DefaultStepperUnit = unit },
            nameof(UpdateFarmSettingsCommand.DefaultStepperUnit)));

    [Fact]
    public void CommaListedEggUnits_Fail() =>
        Assert.True(Fails(Valid() with { DefaultStepperUnit = "Tray,Case" },
            nameof(UpdateFarmSettingsCommand.DefaultStepperUnit)));

    // --- format overrides -------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dd/MM/yyyy")]
    [InlineData("d")]
    public void UsableDateFormat_Passes(string? format) =>
        Assert.True(_validator.Validate(Valid() with { DateFormatOverride = format }).IsValid);

    [Fact]
    public void UnusableDateFormat_Fails() =>
        // A lone unknown standard specifier throws when used — it must not
        // reach a screen that formats dates on every row.
        Assert.True(Fails(Valid() with { DateFormatOverride = "q" },
            nameof(UpdateFarmSettingsCommand.DateFormatOverride)));

    [Fact]
    public void UnusableTimeFormat_Fails() =>
        Assert.True(Fails(Valid() with { TimeFormatOverride = "q" },
            nameof(UpdateFarmSettingsCommand.TimeFormatOverride)));

    [Fact]
    public void NegativeVersion_Fails() =>
        Assert.True(Fails(Valid() with { Version = -1 },
            nameof(UpdateFarmSettingsCommand.Version)));
}
