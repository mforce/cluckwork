namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

using System.Globalization;
using Cluckwork.Domain.Accounts;
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
            .MaximumLength(Account.MaxNameLength);

        // The timezone gate that keeps #35's fail-closed path unreachable in
        // practice: FarmClock refuses to guess a date when the stored zone is
        // unusable, so an unusable zone must never be storable. Same test the
        // clock itself runs.
        RuleFor(x => x.TimeZoneId)
            .Must(tz => !string.IsNullOrWhiteSpace(tz))
            .WithMessage("A timezone is required.")
            .MaximumLength(Account.MaxTimeZoneIdLength)
            .Must(BeAKnownTimeZone)
            .WithMessage("Timezone must be a valid IANA timezone id, for example America/Los_Angeles.");

        RuleFor(x => x.Locale)
            .Must(l => !string.IsNullOrWhiteSpace(l))
            .WithMessage("A locale is required.")
            .MaximumLength(Account.MaxLocaleLength)
            .Must(BeASpecificCulture)
            .WithMessage("Locale must be a BCP 47 tag that includes a region, for example en-US, es-MX or ja-JP.");

        // Format only. An unlisted-but-well-formed code is legal and takes the
        // §4.6 fallback (symbol = code, minor unit = 2) rather than a 400.
        RuleFor(x => x.CurrencyCode)
            .Must(CurrencyCatalog.IsWellFormedCode)
            .WithMessage("Currency must be a three-letter ISO 4217 code, for example USD.");

        RuleFor(x => x.UnitSystem)
            .Must(BeEnumName<UnitSystem>)
            .WithMessage("Unit system must be Metric or Imperial.");

        RuleFor(x => x.FirstDayOfWeek)
            .Must(d => string.IsNullOrWhiteSpace(d) || BeEnumName<DayOfWeek>(d))
            .WithMessage("First day of week must be a day name, for example Monday, or empty to follow the locale.");

        RuleFor(x => x.DateFormatOverride)
            .MaximumLength(Account.MaxFormatOverrideLength)
            .Must(BeAUsableFormat)
            .WithMessage("Date format is not a usable .NET format string.");

        RuleFor(x => x.TimeFormatOverride)
            .MaximumLength(Account.MaxFormatOverrideLength)
            .Must(BeAUsableFormat)
            .WithMessage("Time format is not a usable .NET format string.");

        RuleFor(x => x.Version)
            .GreaterThanOrEqualTo(0);
    }

    // Names only. Enum.TryParse also accepts the underlying numbers, so a bare
    // "0" would quietly mean Metric — the API's wire contract is the name
    // everywhere else, and an ordinal is not something a client should be able
    // to rely on across an enum reorder.
    private static bool BeEnumName<TEnum>(string? value) where TEnum : struct, Enum =>
        value is { Length: > 0 }
        && char.IsLetter(value[0])
        && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed);

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
