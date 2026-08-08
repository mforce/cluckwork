namespace Cluckwork.Application.Features.Accounts.UpdateFarmSettings;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Catalog;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;

public sealed class UpdateFarmSettingsHandler(
    IAccountRepository accounts,
    ICurrencyBoundRowProbe currencyBoundRows,
    IEggUnitConversionRepository conversions,
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

        // #444 — same reason as AddOrderItemHandler's identical check: a farm
        // default pointing at a deactivated (or never-activated) conversion
        // would silently produce +1/-1 with no explanation. Account.UpdateSettings
        // has no repository access, so this is decided here, like financialRowsExist.
        var stepperUnit = Enum.Parse<EggUnit>(command.DefaultStepperUnit, ignoreCase: true);
        var stepperConversion = await conversions.GetByUnitAsync(stepperUnit, ct);
        if (stepperConversion is null || !stepperConversion.Active)
            return Result.Failure(Error.Validation(
                "FarmSettings.NoUnitConversion",
                $"No active eggs-per-unit definition for '{stepperUnit}' — set one on the Products screen."));

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
        // moments, and another request could once book the farm's first sale
        // or expense in between.
        //
        // What does NOT fix this, tested rather than assumed
        // (CurrencyLockSerializationTests): running THIS transaction at
        // SERIALIZABLE. Postgres only tracks read-write conflicts among
        // transactions that are all serializable, and every money-writing
        // handler runs at the default isolation. The interleaving is invisible
        // to SSI and commits happily.
        //
        // #162 closes it with row locks instead. FOR UPDATE here; FOR SHARE in
        // every handler that stamps the currency onto a new row, inside its
        // insert's transaction. Holding the exclusive lock means no money
        // writer is mid-flight (its FOR SHARE would have blocked us) and none
        // can start until we commit (our lock blocks its FOR SHARE — it then
        // reads the NEW currency). So the single probe below, taken after the
        // lock, is authoritative: what it sees is all there is.
        // CurrencyLockRaceTests drives both interleavings against the real
        // handlers. A lock-level failure (deadlock, 40P01) surfaces through
        // the global PostgresException→409 mapping.
        Result result = Result.Success();
        var committed = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            // Result discarded on purpose. The call exists for the lock; the
            // identity map hands back the SAME instance as `account` above
            // and deliberately does not refresh its values, so capturing it
            // would change nothing. Staleness is guarded elsewhere: Version
            // is an EF concurrency token, so an interleaved settings save
            // makes THIS save throw DbUpdateConcurrencyException (→ the
            // global 409) instead of silently losing the other's write.
            await accounts.GetCurrentLockedAsync(token);

            var before = Snapshot(account);
            result = Apply(account, command, await currencyBoundRows.AnyAsync(token));
            if (result.IsFailure) return false;

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
            command.Brand,
            Enum.Parse<EggUnit>(command.DefaultStepperUnit, ignoreCase: true),
            currencyBoundRowsExist);

    // Same SaveChanges as the change (#93). Settings decide how every date and
    // every amount on the farm is read, so the trail records the whole block on
    // both sides, not just "someone saved".
    private Task WriteAuditAsync(Account account, object before, CancellationToken ct) =>
        audit.WriteAsync(
            AuditActions.AccountUpdateSettings, nameof(Account), account.Id,
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
        a.TimeFormatOverride,
        a.Brand,
        DefaultStepperUnit = a.DefaultStepperUnit.ToString()
    };
}
