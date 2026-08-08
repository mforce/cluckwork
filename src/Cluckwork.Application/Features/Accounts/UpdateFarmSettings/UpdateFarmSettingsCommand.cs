namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

// The whole §4.5 settings block, replaced under the account's Version token.
// UnitSystem and FirstDayOfWeek travel as strings because the API serializes
// enums by name everywhere else (order status, movement type, water source);
// the validator proves they parse before the handler converts them.
public sealed record UpdateFarmSettingsCommand(
    string Name,
    string TimeZoneId,
    string Locale,
    string CurrencyCode,
    string UnitSystem,
    string? FirstDayOfWeek,
    string? DateFormatOverride,
    string? TimeFormatOverride,
    // Curated palette id, validated in the aggregate rather than here so the
    // failure surfaces as 422-with-a-code (#149).
    string Brand,
    // #444 — the farm-default Daily Entry stepper pack unit (e.g. "Tray"),
    // travels as a name like UnitSystem/FirstDayOfWeek above.
    string DefaultStepperUnit,
    int Version);
