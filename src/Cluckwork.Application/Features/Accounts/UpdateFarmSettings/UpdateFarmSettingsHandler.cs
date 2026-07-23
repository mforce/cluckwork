namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

using System.Data;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;

public sealed class UpdateFarmSettingsHandler(
    IAccountRepository accounts,
    ICurrencyBoundRowProbe currencyBoundRows,
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

        // The ordinary save — a rename, a locale, a timezone — has nothing to
        // read-then-decide, so it stays a plain write. §4.6 only gates a
        // currency CHANGE, and only that path pays for the probe or the
        // stricter isolation below.
        if (!currencyChanging)
        {
            var before = Snapshot(account);
            var plain = Apply(account, command, currencyBoundRowsExist: false);
            if (plain.IsFailure) return plain;
            await WriteAuditAsync(account, before, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }

        // A currency change decides on the strength of "this farm has no money
        // rows at all" and then writes the new currency. Under READ COMMITTED
        // those are two moments: another request can book the farm's first sale
        // or expense in between, and the change commits anyway — leaving rows
        // in one denomination and the farm in another, which is precisely what
        // §4.6 exists to prevent.
        //
        // SERIALIZABLE closes it: the probe's scans take predicate locks, so a
        // concurrent insert into any of those tables makes one of the two
        // transactions fail to serialize (SQLSTATE 40001 → 409, same as any
        // other concurrency conflict). It costs nothing in practice — a farm
        // changes currency about once, at setup.
        Result result = Result.Success();
        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var exists = await currencyBoundRows.AnyAsync(token);
            var before = Snapshot(account);

            result = Apply(account, command, exists);
            if (result.IsFailure) return false;

            await WriteAuditAsync(account, before, token);
            return true;
        }, IsolationLevel.Serializable, ct);

        return result;
    }

    private static Result Apply(
        Account account, UpdateFarmSettingsCommand command, bool currencyBoundRowsExist) =>
        account.UpdateSettings(
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
            currencyBoundRowsExist);

    // Same SaveChanges as the change (#93). Settings decide how every date and
    // every amount on the farm is read, so the trail records the whole block on
    // both sides, not just "someone saved".
    private Task WriteAuditAsync(Account account, object before, CancellationToken ct) =>
        audit.WriteAsync(
            "Account.UpdateSettings", nameof(Account), account.Id,
            reason: null,
            details: new { before, after = Snapshot(account) },
            ct: ct);

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
