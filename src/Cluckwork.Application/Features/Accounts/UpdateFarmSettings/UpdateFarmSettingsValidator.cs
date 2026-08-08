namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

using System.Globalization;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class UpdateFarmSettingsValidator : AbstractValidator<UpdateFarmSettingsCommand>
{
    // A sample instant with an unambiguous day, month, hour and minute, so a
    // format string that only breaks on one of them still throws here.
    private static readonly DateTimeOffset FormatProbe =
        new(2026, 12, 31, 23, 45, 56, TimeSpan.Zero);

    public UpdateFarmSettingsValidator()
    {
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("A farm name is required.")
            .WithErrorCode("FarmSettings.Name.Required")
            .MaximumLength(Account.MaxNameLength)
            .WithErrorCode("FarmSettings.Name.MaxLength");

        // The timezone gate that keeps #35's fail-closed path unreachable in
        // practice: FarmClock refuses to guess a date when the stored zone is
        // unusable, so an unusable zone must never be storable. Same test the
        // clock itself runs.
        RuleFor(x => x.TimeZoneId)
            .Must(tz => !string.IsNullOrWhiteSpace(tz))
            .WithMessage("A timezone is required.")
            .WithErrorCode("FarmSettings.TimeZoneId.Required")
            .MaximumLength(Account.MaxTimeZoneIdLength)
            .WithErrorCode("FarmSettings.TimeZoneId.MaxLength")
            .Must(BeAKnownTimeZone)
            .WithMessage("Timezone must be a valid IANA timezone id, for example America/Los_Angeles.")
            .WithErrorCode("FarmSettings.TimeZoneId.Known");

        RuleFor(x => x.Locale)
            .Must(l => !string.IsNullOrWhiteSpace(l))
            .WithMessage("A locale is required.")
            .WithErrorCode("FarmSettings.Locale.Required")
            .MaximumLength(Account.MaxLocaleLength)
            .WithErrorCode("FarmSettings.Locale.MaxLength")
            .Must(BeASpecificCulture)
            .WithMessage("Locale must be a BCP 47 tag that includes a region, for example en-US, es-MX or ja-JP.")
            .WithErrorCode("FarmSettings.Locale.Format");

        // Format only. An unlisted-but-well-formed code is legal and takes the
        // §4.6 fallback (symbol = code, minor unit = 2) rather than a 400.
        RuleFor(x => x.CurrencyCode)
            .Must(CurrencyCatalog.IsWellFormedCode)
            .WithMessage("Currency must be a three-letter ISO 4217 code, for example USD.")
            .WithErrorCode("FarmSettings.CurrencyCode.Format");

        RuleFor(x => x.UnitSystem)
            .Must(BeEnumName<UnitSystem>)
            .WithMessage("Unit system must be Metric or Imperial.")
            .WithErrorCode("FarmSettings.UnitSystem.Allowed");

        RuleFor(x => x.FirstDayOfWeek)
            .Must(d => string.IsNullOrWhiteSpace(d) || BeEnumName<DayOfWeek>(d))
            .WithMessage("First day of week must be a day name, for example Monday, or empty to follow the locale.")
            .WithErrorCode("FarmSettings.FirstDayOfWeek.Allowed");

        RuleFor(x => x.DateFormatOverride)
            .MaximumLength(Account.MaxFormatOverrideLength)
            .WithErrorCode("FarmSettings.DateFormatOverride.MaxLength")
            .Must(BeAUsableFormat)
            .WithMessage("Date format is not a usable .NET format string.")
            .WithErrorCode("FarmSettings.DateFormatOverride.Format");

        RuleFor(x => x.TimeFormatOverride)
            .MaximumLength(Account.MaxFormatOverrideLength)
            .WithErrorCode("FarmSettings.TimeFormatOverride.MaxLength")
            .Must(BeAUsableFormat)
            .WithMessage("Time format is not a usable .NET format string.")
            .WithErrorCode("FarmSettings.TimeFormatOverride.Format");

        RuleFor(x => x.DefaultStepperUnit)
            .Must(BeEnumName<EggUnit>)
            .WithMessage("Default stepper unit must be one of the farm's egg units, for example Individual or Tray.")
            .WithErrorCode("FarmSettings.DefaultStepperUnit.Allowed");

        RuleFor(x => x.Version)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode("FarmSettings.Version.NonNegative");
    }

    // Exactly one name, nothing else. Enum.TryParse is far more permissive than
    // it looks: it accepts the underlying number ("0" → Metric) and — for any
    // enum, flags or not — a comma-separated list whose values it ORs together,
    // so "Monday,Tuesday" parses to Wednesday and passes Enum.IsDefined. Both
    // would be stored as something the caller never asked for.
    //
    // Round-tripping the parse back to its name is the check that admits only
    // what the wire contract actually offers.
    private static bool BeEnumName<TEnum>(string? value) where TEnum : struct, Enum =>
        value is { Length: > 0 }
        && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool BeAKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException
                                      or InvalidTimeZoneException
                                      or ArgumentException)
        {
            return false;
        }
    }

    private static bool BeASpecificCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return false;
        try
        {
            // predefinedOnly: without it the runtime happily manufactures a
            // culture for any well-shaped tag, so "zz-ZZ" would validate.
            var culture = CultureInfo.GetCultureInfo(locale.Trim(), predefinedOnly: true);
            return !culture.IsNeutralCulture && culture.LCID != CultureInfo.InvariantCulture.LCID;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    // Empty means "no override". A non-empty one has to survive being used —
    // an unparseable format string would otherwise throw at render time on
    // every screen that formats a date.
    private static bool BeAUsableFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return true;
        try
        {
            FormatProbe.ToString(format.Trim(), CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
