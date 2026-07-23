namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;

public sealed class UpdateFarmSettingsHandler(
    IAccountRepository accounts,
    IFinancialRowProbe financialRows,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(UpdateFarmSettingsCommand command, CancellationToken ct)
    {
        // Tracked, so the mutation below is enough — no repository Update call.
        var account = await accounts.GetCurrentForUpdateAsync(ct);
        if (account is null)
            return Result.Failure(Error.NotFound(nameof(Account), "current"));

        if (command.Version != account.Version)
            return Result.Failure(Error.Conflict(
                "Account.VersionMismatch",
                "These settings were changed by someone else. Reload them and reapply your edit."));

        var currencyChanging = !string.Equals(
            command.CurrencyCode.Trim(), account.DefaultCurrencyCode, StringComparison.OrdinalIgnoreCase);
        // Only pay for the probe when the answer can matter (§4.6 only gates a
        // currency CHANGE); a plain name or locale save skips three EXISTS.
        var financialRowsExist = currencyChanging && await financialRows.AnyFinancialRowsAsync(ct);

        var before = Snapshot(account);

        var result = account.UpdateSettings(
            command.Name,
            command.TimeZoneId,
            command.Locale,
            command.CurrencyCode,
            Enum.Parse<UnitSystem>(command.UnitSystem, ignoreCase: true),
            string.IsNullOrWhiteSpace(command.FirstDayOfWeek)
                ? null
                : Enum.Parse<DayOfWeek>(command.FirstDayOfWeek, ignoreCase: true),
            command.DateFormatOverride,
            command.TimeFormatOverride,
            financialRowsExist);
        if (result.IsFailure) return result;

        // Same SaveChanges as the change (#93). Settings decide how every date
        // and every amount on the farm is read, so the trail records the whole
        // block on both sides, not just "someone saved".
        await audit.WriteAsync(
            "Account.UpdateSettings", nameof(Account), account.Id,
            reason: null,
            details: new { before, after = Snapshot(account) },
            ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static object Snapshot(Account a) => new
    {
        a.Name,
        a.TimeZoneId,
        a.Locale,
        CurrencyCode = a.DefaultCurrencyCode,
        CurrencyMinorUnit = a.DefaultCurrencyMinorUnit,
        UnitSystem = a.UnitSystem.ToString(),
        FirstDayOfWeek = a.FirstDayOfWeek?.ToString(),
        a.DateFormatOverride,
        a.TimeFormatOverride
    };
}
