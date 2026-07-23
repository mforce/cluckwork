namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;

// #123 — the settings block and the two rules that guard it: §4.6's currency
// lock and the derivation that runs when the lock is open.
public sealed class AccountSettingsTests
{
    private static Account UsdFarm() =>
        Account.Create(Guid.NewGuid(), "Test Farm Co", "UTC", "USD");

    private static Result Update(
        Account account,
        string? name = null,
        string? timeZoneId = null,
        string? locale = null,
        string? currencyCode = null,
        UnitSystem unitSystem = UnitSystem.Metric,
        DayOfWeek? firstDayOfWeek = null,
        string? dateFormatOverride = null,
        string? timeFormatOverride = null,
        bool financialRowsExist = false) =>
        account.UpdateSettings(
            name ?? account.Name,
            timeZoneId ?? account.TimeZoneId,
            locale ?? account.Locale,
            currencyCode ?? account.DefaultCurrencyCode,
            unitSystem, firstDayOfWeek, dateFormatOverride, timeFormatOverride,
            financialRowsExist);

    [Fact]
    public void UpdateSettings_AppliesTheBlock_AndBumpsVersion()
    {
        var account = UsdFarm();
        var before = account.Version;

        var result = Update(account,
            name: "  Sunrise Layers  ",
            timeZoneId: "America/Los_Angeles",
            locale: "es-MX",
            unitSystem: UnitSystem.Imperial,
            firstDayOfWeek: DayOfWeek.Monday,
            dateFormatOverride: "  dd/MM/yyyy  ");

        Assert.True(result.IsSuccess);
        Assert.Equal("Sunrise Layers", account.Name);          // trimmed
        Assert.Equal("America/Los_Angeles", account.TimeZoneId);
        Assert.Equal("es-MX", account.Locale);
        Assert.Equal(UnitSystem.Imperial, account.UnitSystem);
        Assert.Equal(DayOfWeek.Monday, account.FirstDayOfWeek);
        Assert.Equal("dd/MM/yyyy", account.DateFormatOverride);
        Assert.Null(account.TimeFormatOverride);
        Assert.Equal(before + 1, account.Version);
    }

    [Fact]
    public void BlankOverrides_ClearBackToNull()
    {
        var account = UsdFarm();
        Update(account, dateFormatOverride: "dd/MM/yyyy", timeFormatOverride: "HH:mm");
        Assert.NotNull(account.DateFormatOverride);

        Update(account, dateFormatOverride: "   ", timeFormatOverride: null);

        Assert.Null(account.DateFormatOverride);
        Assert.Null(account.TimeFormatOverride);
    }

    // --- §4.6 currency lock -----------------------------------------------

    [Fact]
    public void CurrencyChange_WithFinancialRows_IsRefused_AndNothingElseIsApplied()
    {
        var account = UsdFarm();
        var version = account.Version;

        var result = Update(account,
            name: "Renamed", currencyCode: "JPY", financialRowsExist: true);

        Assert.True(result.IsFailure);
        Assert.Equal("Account.CurrencyLocked", result.Error.Code);
        // The whole save is refused, not just the currency field.
        Assert.Equal("Test Farm Co", account.Name);
        Assert.Equal("USD", account.DefaultCurrencyCode);
        Assert.Equal(version, account.Version);
    }

    [Fact]
    public void NonCurrencyEdit_WithFinancialRows_IsAllowed()
    {
        var account = UsdFarm();

        var result = Update(account, name: "Renamed", locale: "ja-JP", financialRowsExist: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", account.Name);
        Assert.Equal("USD", account.DefaultCurrencyCode);
    }

    [Fact]
    public void SameCurrencyInDifferentCase_IsNotAChange()
    {
        var account = UsdFarm();

        var result = Update(account, currencyCode: "usd", financialRowsExist: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("USD", account.DefaultCurrencyCode);
    }

    // --- §4.6 derivation on an allowed change ------------------------------

    [Fact]
    public void AllowedCurrencyChange_RederivesSymbolAndMinorUnit()
    {
        var account = UsdFarm();
        Assert.Equal(2, account.DefaultCurrencyMinorUnit);

        var result = Update(account, currencyCode: "JPY", financialRowsExist: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("JPY", account.DefaultCurrencyCode);
        Assert.Equal(0, account.DefaultCurrencyMinorUnit);   // yen has no minor unit
        Assert.Equal("¥", account.CurrencySymbol);
    }

    [Fact]
    public void UnknownButWellFormedCode_TakesTheFallback()
    {
        var account = UsdFarm();

        var result = Update(account, currencyCode: "zzz");

        Assert.True(result.IsSuccess);
        Assert.Equal("ZZZ", account.DefaultCurrencyCode);
        Assert.Equal("ZZZ", account.CurrencySymbol);          // symbol = code
        Assert.Equal(2, account.DefaultCurrencyMinorUnit);    // minor unit = 2
    }

    [Fact]
    public void UnchangedCurrency_DoesNotTouchTheStoredMinorUnit()
    {
        // Guards the reason the derivation is conditional: re-deriving on every
        // save would let a catalog change silently reinterpret stored money.
        var account = Account.Create(Guid.NewGuid(), "Yen Farm", "Asia/Tokyo", "JPY");
        Assert.Equal(0, account.DefaultCurrencyMinorUnit);

        Update(account, name: "Yen Farm 2");

        Assert.Equal(0, account.DefaultCurrencyMinorUnit);
    }

    // --- required fields (§4.5: an active farm needs all three) ------------

    [Theory]
    [InlineData("", "UTC", "en-US", "USD", "Account.NameRequired")]
    [InlineData("   ", "UTC", "en-US", "USD", "Account.NameRequired")]
    [InlineData("Farm", "", "en-US", "USD", "Account.TimeZoneRequired")]
    [InlineData("Farm", "UTC", "  ", "USD", "Account.LocaleRequired")]
    [InlineData("Farm", "UTC", "en-US", "US", "Account.CurrencyCodeInvalid")]
    [InlineData("Farm", "UTC", "en-US", "US1", "Account.CurrencyCodeInvalid")]
    [InlineData("Farm", "UTC", "en-US", "", "Account.CurrencyCodeInvalid")]
    public void MissingRequiredField_Fails(
        string name, string timeZoneId, string locale, string currencyCode, string expectedCode)
    {
        var account = UsdFarm();

        var result = account.UpdateSettings(
            name, timeZoneId, locale, currencyCode,
            UnitSystem.Metric, null, null, null, financialRowsExist: false);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public void OverlongName_Fails()
    {
        var account = UsdFarm();

        var result = Update(account, name: new string('x', Account.MaxNameLength + 1));

        Assert.True(result.IsFailure);
        Assert.Equal("Account.NameTooLong", result.Error.Code);
    }
}
