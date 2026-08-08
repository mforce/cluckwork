namespace Cluckwork.Domain.Eggs;

public sealed class EggLot : AggregateRoot<Guid>
{
    public Guid FlockId { get; private set; }
    public DateOnly ProductionDate { get; private set; }
    public Guid EggGradeId { get; private set; }
    public int QuantityProduced { get; private set; }
    public int QuantityAvailable { get; private set; }

    // The daily entry whose submit generated this lot (#69) — the adjust/void
    // reconciliation surface. (flock, date) is NOT enough: multiple houses
    // mean multiple entries per flock and day. Null on lots that predate the
    // linkage and couldn't be backfilled unambiguously; their entries refuse
    // adjust/void, like pre-#60 orders refuse void.
    public Guid? DailyEntryId { get; private set; }

    // Null = unrestricted. Set when medication withdrawal applies.
    public DateOnly? RestrictedUntil { get; private set; }

    // Row-version token for optimistic concurrency on reads;
    // sales-allocation path uses pessimistic FOR UPDATE (tech spec §3.3).
    public int Version { get; private set; }

    private EggLot() { }

    public static EggLot Create(
        Guid id, Guid accountId, Guid flockId,
        DateOnly productionDate, Guid eggGradeId, int quantity,
        Guid? dailyEntryId = null)
    {
        if (eggGradeId == Guid.Empty)
            throw new ArgumentException("Egg grade id is required.", nameof(eggGradeId));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Lot quantity must be positive.");

        return new EggLot
        {
            Id = id, AccountId = accountId,
            FlockId = flockId, ProductionDate = productionDate,
            EggGradeId = eggGradeId,
            QuantityProduced = quantity, QuantityAvailable = quantity,
            DailyEntryId = dailyEntryId
        };
    }

    public bool IsRestricted(DateOnly asOfDate) =>
        RestrictedUntil.HasValue && RestrictedUntil.Value >= asOfDate;

    public Result SetWithdrawalRestriction(DateOnly restrictedUntil)
    {
        RestrictedUntil = restrictedUntil;
        Version++;
        return Result.Success();
    }

    public Result ClearWithdrawalRestriction()
    {
        RestrictedUntil = null;
        Version++;
        return Result.Success();
    }

    // Called inside the pessimistic FOR UPDATE transaction (tech spec §3.3).
    // Re-validates after acquiring the lock; don't call from outside that tx.
    public Result Allocate(int quantity, DateOnly allocationDate)
    {
        if (IsRestricted(allocationDate))
            return Result.Failure(Error.Domain(
                "EggLot.WithdrawalRestricted",
                "This egg lot is under withdrawal restriction and cannot be sold."));

        // Eggs that have not been produced yet cannot be sold. The FIFO query
        // filters these out too; this is the domain's own guard so the rule does
        // not live only in SQL (#35). Such lots exist in older data — the
        // +1-day validator slack let an entry be dated tomorrow.
        if (ProductionDate > allocationDate)
            return Result.Failure(Error.Domain(
                "EggLot.NotYetProduced",
                "This egg lot is dated in the future and cannot be sold yet."));

        if (quantity <= 0)
            return Result.Failure(Error.Validation(
                "EggLot.InvalidQuantity", "Allocation quantity must be positive."));

        if (quantity > QuantityAvailable)
            return Result.Failure(Error.Domain(
                "EggLot.InsufficientStock",
                $"Requested {quantity} but only {QuantityAvailable} available."));

        QuantityAvailable -= quantity;
        Version++;
        return Result.Success();
    }

    // Entry adjust/void reconciliation (#69): re-state what this lot produced.
    // The sold/allocated portion (Produced − Available) is untouchable — the
    // eggs left the farm — so production can never be set below it; available
    // absorbs the whole delta. Setting 0 empties an unsold lot (entry void or
    // a grade line removed); the row stays for provenance. Call only inside
    // the pessimistic FOR UPDATE transaction, like Allocate/Restore.
    public Result AdjustProduction(int newQuantity)
    {
        if (newQuantity < 0)
            return Result.Failure(Error.Validation(
                "EggLot.InvalidQuantity", "Adjusted production cannot be negative."));

        var sold = QuantityProduced - QuantityAvailable;
        if (newQuantity < sold)
            return Result.Failure(Error.Domain(
                "EggLot.SoldExceedsAdjusted",
                $"{sold} eggs from this lot are already sold, allocated, or written off; production cannot be set below that."));

        QuantityProduced = newQuantity;
        QuantityAvailable = newQuantity - sold;
        Version++;
        return Result.Success();
    }

    // Standalone stock correction (#406): write-off (breakage, spoilage,
    // internal use) or reconciliation recount. Available moves within
    // [0, Produced]; production is a fact about the day's laying this method
    // never restates — beyond-produced recounts belong to AdjustProduction
    // via the daily entry. Withdrawal restriction is intentionally not
    // checked: it protects sales, and removing stock is the safe direction.
    // Call only inside the pessimistic FOR UPDATE transaction, like Allocate.
    public Result AdjustAvailable(int delta)
    {
        if (delta == 0)
            return Result.Failure(Error.Validation(
                "EggLot.InvalidQuantity", "Adjustment quantity cannot be zero."));

        // 64-bit sums: near-int.MaxValue lots would wrap the 32-bit addition
        // negative and sail past both guards (security review of #406).
        if (delta < 0 && -(long)delta > QuantityAvailable)
            return Result.Failure(Error.Domain(
                "EggLot.InsufficientStock",
                $"Cannot remove {-(long)delta}: only {QuantityAvailable} available in this lot."));

        // Coarse ceiling only — the handler additionally caps a positive delta
        // at the lot's cumulative write-off total, because allocation lowers
        // Available without touching Produced and this guard cannot see it.
        if (delta > 0 && QuantityAvailable + (long)delta > QuantityProduced)
            return Result.Failure(Error.Domain(
                "EggLot.ReconcileExceedsProduced",
                $"Adding {delta} would exceed the {QuantityProduced} produced in this lot; a recount above production is a daily-entry adjustment."));

        QuantityAvailable += delta;
        Version++;
        return Result.Success();
    }

    // Inverse of Allocate, for voiding a confirmed sale (#60). Same rule as
    // Allocate: call only inside the pessimistic FOR UPDATE transaction.
    // Withdrawal restriction is intentionally not checked — the eggs return to
    // the lot they came from and keep whatever restriction it carries.
    public Result Restore(int quantity)
    {
        if (quantity <= 0)
            return Result.Failure(Error.Validation(
                "EggLot.InvalidQuantity", "Restore quantity must be positive."));

        if (QuantityAvailable + (long)quantity > QuantityProduced)
            return Result.Failure(Error.Domain(
                "EggLot.RestoreExceedsProduced",
                $"Restoring {quantity} would exceed the {QuantityProduced} produced in this lot."));

        QuantityAvailable += quantity;
        Version++;
        return Result.Success();
    }
}
