namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

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
        var account = await accounts.GetCurrentTrackedAsync(ct);
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
        // currency CHANGE, and only that path pays for the probes below.
        if (!currencyChanging)
        {
            var before = Snapshot(account);
            var plain = Apply(account, command, currencyBoundRowsExist: false);
            if (plain.IsFailure) return plain;
            await WriteAuditAsync(account, before, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }

        // A currency change decides on the strength of "this farm has recorded
        // no amount at all" and then writes the new currency. Those are two
        // moments, and another request can book the farm's first sale or
        // expense in between.
        //
        // What does NOT fix this, tested rather than assumed
        // (CurrencyLockSerializationTests): running THIS transaction at
        // SERIALIZABLE. Postgres only tracks read-write conflicts among
        // transactions that are all serializable, and every money-writing
        // handler runs at the default isolation — several do their currency
        // read and their insert as separate autocommit statements. The
        // interleaving is therefore invisible to SSI and commits happily. A
        // real close needs every money-writing path to take a shared lock on
        // the account row it reads the currency from; that is a change across
        // half a dozen handlers on a hot row, and it belongs in its own slice.
        //
        // What this does instead is bound the window. Both probes run inside
        // the transaction, one before the decision and one immediately before
        // the commit; under READ COMMITTED each statement takes a fresh
        // snapshot, so the second sees anything committed since the first. What
        // is left is the gap between that last probe and the commit — sub-
        // millisecond, against a window that was previously the whole handler.
        Result result = Result.Success();
        var committed = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var before = Snapshot(account);

            result = Apply(account, command, await currencyBoundRows.AnyAsync(token));
            if (result.IsFailure) return false;

            // Last look before committing. A row that landed while we were
            // deciding still refuses the change rather than stranding itself in
            // the old denomination.
            if (await currencyBoundRows.AnyAsync(token))
            {
                result = Result.Failure(CurrencyLandedMidFlight);
                return false;
            }

            await WriteAuditAsync(account, before, token);
            return true;
        }, ct);

        if (!committed)
        {
            // The rollback undid the row, but the tracked entity still carries
            // the new currency; anything that saves later in this request — the
            // idempotency record, for one — would flush it (pi review of #159).
            accounts.DiscardChanges(account);

            // The two only agree because every `return false` above sets
            // `result` first. Adding a third and forgetting would report a
            // rolled-back save as 204, so don't rely on remembering.
            if (result.IsSuccess)
                result = Result.Failure(Error.Conflict(
                    "Account.SettingsNotSaved",
                    "The settings could not be saved. Reload them and try again."));
        }

        return result;
    }

    private static readonly Error CurrencyLandedMidFlight = Error.Conflict(
        "Account.CurrencyLocked",
        "The farm currency cannot be changed once sales orders, payments, expenses, priced " +
        "products or feed costs exist. One was recorded while these settings were being saved.");

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
            // TODO(#149 Task 9): carry the command's Brand through instead.
            FarmBrands.Default,
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
