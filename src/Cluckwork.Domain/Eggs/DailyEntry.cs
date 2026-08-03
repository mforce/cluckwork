namespace Cluckwork.Domain.Eggs;

using System.Text.Json;

public sealed class DailyEntry : AggregateRoot<Guid>
{
    public const int MaxReasonLength = 500;

    private readonly List<DailyEntryGrade> _grades = [];

    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public Guid FlockId { get; private set; }
    public DateOnly Date { get; private set; }
    public DailyEntryStatus Status { get; private set; }

    public int TotalEggs { get; private set; }
    public int CrackedEggs { get; private set; }
    public int DirtyEggs { get; private set; }
    public int DiscardedEggs { get; private set; }
    public int MortalityCount { get; private set; }

    // Sellable production by grade (Phase 1: eggs are graded at collection).
    public IReadOnlyList<DailyEntryGrade> Grades => _grades.AsReadOnly();

    // Audit trail until the audit-log slice lands (#69): the last adjust's
    // reason + the values it replaced (JSON snapshot), the void reason, and
    // when the auto-lock job locked the entry.
    public string? AdjustReason { get; private set; }
    public string? AdjustedFromJson { get; private set; }
    public string? VoidReason { get; private set; }
    public DateTimeOffset? LockedAtUtc { get; private set; }

    // Optimistic concurrency token — functional spec §10.9.1
    public int Version { get; private set; }

    private DailyEntry() { }

    // Unique constraint: (account_id, farm_id, house_id, flock_id, date)
    public static DailyEntry Create(
        Guid id, Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date)
    {
        return new DailyEntry
        {
            Id = id, AccountId = accountId,
            FarmId = farmId, HouseId = houseId,
            FlockId = flockId, Date = date,
            Status = DailyEntryStatus.Draft
        };
    }

    public Result RecordProduction(
        int totalEggs, int cracked, int dirty, int discarded, int mortality,
        IReadOnlyCollection<GradeQuantity>? grades = null)
    {
        // Draft-only: submitting generates egg lots from the grade lines, and
        // editing a submitted entry would silently diverge from its (possibly
        // partially sold) lots. Post-submit corrections are the manager-adjust
        // flow with lot reconciliation (#69). (Spec §8.1's
        // worker-edit-until-cutoff moved there too.)
        if (Status != DailyEntryStatus.Draft)
            return Result.Failure(Error.Domain(
                "DailyEntry.Immutable", "Only draft entries can be edited."));

        // null = leave existing grade lines untouched (older clients omit the
        // field); the kept lines must still fit the new totals. [] clears.
        // Drafts stay flexible (#394): grading may be incomplete or absent right
        // up until submit, so this is the lenient "cannot exceed" check only.
        var effectiveGrades = grades ?? CurrentGradeQuantities();
        var gradeResult = ValidateGrades(totalEggs, cracked, dirty, discarded, effectiveGrades);
        if (gradeResult.IsFailure) return gradeResult;

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        if (grades is not null)
            ReplaceGrades(grades);
        Version++;
        return Result.Success();
    }

    public Result Submit()
    {
        if (Status != DailyEntryStatus.Draft)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotDraft", "Only draft entries can be submitted."));

        // #394 — a draft may be incomplete or entirely ungraded, but submitting
        // freezes it and turns the grade lines into the day's only stock: an
        // ungraded submit would silently produce zero lots for real production.
        // Grades must reconcile EXACTLY to the sellable count here (zero sellable
        // reconciles to zero lines), unlike RecordProduction's lenient "cannot
        // exceed" check above.
        var gradeResult = ValidateGrades(
            TotalEggs, CrackedEggs, DirtyEggs, DiscardedEggs, CurrentGradeQuantities(),
            requireExactReconciliation: true);
        if (gradeResult.IsFailure) return gradeResult;

        Status = DailyEntryStatus.Submitted;
        // Version is a concurrency token, not auto-incremented by EF: without this
        // bump, two racing submits both match WHERE Version = N and both succeed —
        // duplicating the generated egg lots.
        Version++;
        return Result.Success();
    }

    public Result Lock(DateTimeOffset lockedAtUtc)
    {
        if (Status != DailyEntryStatus.Submitted)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotSubmitted", "Only submitted entries can be locked."));
        Status = DailyEntryStatus.Locked;
        LockedAtUtc = lockedAtUtc;
        Version++;
        return Result.Success();
    }

    // NOTE: like RecordDailyEntryHandler, the adjust handler must validate
    // grade ids against the tenant's grades for the entry's farm before
    // calling this — the aggregate can't check ownership. It must also
    // reconcile the entry's egg lots and bird ledger in the same transaction.
    //
    // Spec §8.1 lets managers edit submitted entries pre-lock; MVP keeps one
    // adjust path for Submitted AND Locked — both land in ManagerAdjusted.
    // Re-adjusting a ManagerAdjusted entry is allowed (corrections can need
    // correcting); each adjust snapshots the values it replaced.
    public Result ManagerAdjust(
        int totalEggs, int cracked, int dirty, int discarded, int mortality, string reason,
        IReadOnlyCollection<GradeQuantity>? grades = null)
    {
        if (Status is not (DailyEntryStatus.Submitted or DailyEntryStatus.Locked
            or DailyEntryStatus.ManagerAdjusted))
            return Result.Failure(Error.Domain(
                "DailyEntry.NotAdjustable",
                "Only submitted, locked, or previously adjusted entries can be adjusted."));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "DailyEntry.ReasonRequired", "An adjustment reason is required."));
        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > MaxReasonLength)
            return Result.Failure(Error.Validation(
                "DailyEntry.ReasonTooLong", $"Reason cannot exceed {MaxReasonLength} characters."));

        // #394 — an adjustment has no draft state of its own to leave
        // incomplete: it replaces the entry's official numbers outright, so it
        // is held to the same exact reconciliation as submit.
        var effectiveGrades = grades ?? CurrentGradeQuantities();
        var gradeResult = ValidateGrades(
            totalEggs, cracked, dirty, discarded, effectiveGrades,
            requireExactReconciliation: true);
        if (gradeResult.IsFailure) return gradeResult;

        AdjustedFromJson = SnapshotJson();
        AdjustReason = trimmedReason;

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        if (grades is not null)
            ReplaceGrades(grades);
        Status = DailyEntryStatus.ManagerAdjusted;
        Version++;
        return Result.Success();
    }

    // Spec §8.1/§9.5: the entry is preserved as Voided; the handler reverses
    // its egg lots and appends the compensating bird movement in the same
    // transaction. Drafts aren't voidable — they never generated anything.
    public Result Void(string reason)
    {
        if (Status is not (DailyEntryStatus.Submitted or DailyEntryStatus.Locked
            or DailyEntryStatus.ManagerAdjusted))
            return Result.Failure(Error.Domain(
                "DailyEntry.NotVoidable",
                "Only submitted, locked, or adjusted entries can be voided."));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "DailyEntry.ReasonRequired", "A void reason is required."));
        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > MaxReasonLength)
            return Result.Failure(Error.Validation(
                "DailyEntry.ReasonTooLong", $"Reason cannot exceed {MaxReasonLength} characters."));

        VoidReason = trimmedReason;
        Status = DailyEntryStatus.Voided;
        Version++;
        return Result.Success();
    }

    // camelCase to match the API's JSON convention — the endpoint embeds this
    // snapshot verbatim in the entry response.
    private string SnapshotJson() => JsonSerializer.Serialize(new
    {
        totalEggs = TotalEggs,
        crackedEggs = CrackedEggs,
        dirtyEggs = DirtyEggs,
        discardedEggs = DiscardedEggs,
        mortalityCount = MortalityCount,
        grades = _grades.Select(g => new { eggGradeId = g.EggGradeId, quantity = g.Quantity }),
    });

    private List<GradeQuantity> CurrentGradeQuantities() =>
        _grades.Select(l => new GradeQuantity(l.EggGradeId, l.Quantity)).ToList();

    // requireExactReconciliation=false (RecordProduction/draft): grades may be
    // incomplete or absent — only "cannot exceed the sellable count" is
    // enforced, and an empty set trivially passes regardless of sellable.
    // requireExactReconciliation=true (Submit/ManagerAdjust, #394): grades must
    // sum to EXACTLY the sellable count — an empty set only passes when
    // sellable is itself zero, so an ungraded non-zero day is refused rather
    // than silently producing no stock.
    private static Result ValidateGrades(
        int totalEggs, int cracked, int dirty, int discarded,
        IReadOnlyCollection<GradeQuantity> grades,
        bool requireExactReconciliation = false)
    {
        if (grades.Count > 0)
        {
            if (grades.Any(g => g.EggGradeId == Guid.Empty))
                return Result.Failure(Error.Validation(
                    "DailyEntry.InvalidGrade", "Egg grade id is required."));

            if (grades.Any(g => g.Quantity <= 0))
                return Result.Failure(Error.Validation(
                    "DailyEntry.InvalidGrade", "Grade quantities must be positive."));

            if (grades.Select(g => g.EggGradeId).Distinct().Count() != grades.Count)
                return Result.Failure(Error.Validation(
                    "DailyEntry.DuplicateGrade", "Each grade may appear only once."));
        }
        else if (!requireExactReconciliation)
        {
            // Lenient path only: zero lines is always a valid (incomplete) draft,
            // regardless of what the counts imply. The exact path falls through —
            // zero sellable is the one case where zero lines is also correct there.
            return Result.Success();
        }

        // Grades are the sellable portion; cracked/dirty/discarded are losses out
        // of the same total. long accumulation — Sum<int> would throw on overflow.
        var sellable = (long)totalEggs - cracked - dirty - discarded;
        var gradedTotal = grades.Sum(g => (long)g.Quantity);

        if (requireExactReconciliation)
        {
            if (gradedTotal != sellable)
                return Result.Failure(Error.Domain(
                    "DailyEntry.GradesNotReconciled",
                    "Graded quantities must equal total eggs minus cracked, dirty, and discarded eggs."));
            return Result.Success();
        }

        if (gradedTotal > sellable)
            return Result.Failure(Error.Domain(
                "DailyEntry.GradesExceedTotal",
                "Graded quantities cannot exceed total eggs minus cracked/dirty/discarded."));

        return Result.Success();
    }

    // Full replace: the daily entry is the single source for the day's grading, so
    // a re-record supersedes previous lines. Reconciled in place (update matching
    // grade, remove gone, add new) rather than clear+add — deleting and re-inserting
    // the same (entry, grade) key in one save can trip the unique index depending on
    // EF's statement ordering.
    private void ReplaceGrades(IReadOnlyCollection<GradeQuantity> grades)
    {
        _grades.RemoveAll(line => grades.All(g => g.EggGradeId != line.EggGradeId));

        foreach (var g in grades)
        {
            var existing = _grades.FirstOrDefault(line => line.EggGradeId == g.EggGradeId);
            if (existing is not null)
                existing.UpdateQuantity(g.Quantity);
            else
                _grades.Add(DailyEntryGrade.Create(AccountId, Id, g.EggGradeId, g.Quantity));
        }
    }
}

public enum DailyEntryStatus { Draft, Submitted, Locked, ManagerAdjusted, Voided }

// Input value for recording production by grade. References an EggGrade row
// (spec §9.2); the handler validates the ids against the account's active
// saleable grades before this reaches the aggregate.
public sealed record GradeQuantity(Guid EggGradeId, int Quantity);

public sealed class DailyEntryGrade : Entity<Guid>
{
    public Guid DailyEntryId { get; private set; }
    public Guid EggGradeId { get; private set; }
    public int Quantity { get; private set; }

    private DailyEntryGrade() { }

    internal void UpdateQuantity(int quantity) => Quantity = quantity;

    // Id is deliberately left unset (EF generates it on add). A client-set key on
    // a line added to an already-tracked entry would be discovered as Modified,
    // producing an UPDATE for a row that doesn't exist yet.
    internal static DailyEntryGrade Create(
        Guid accountId, Guid dailyEntryId, Guid eggGradeId, int quantity)
    {
        return new DailyEntryGrade
        {
            AccountId = accountId,
            DailyEntryId = dailyEntryId,
            EggGradeId = eggGradeId,
            Quantity = quantity
        };
    }
}
