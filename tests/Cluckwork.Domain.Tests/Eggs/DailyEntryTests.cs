namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

public sealed class DailyEntryTests
{
    private static DailyEntry MakeDraft() =>
        DailyEntry.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today));

    private static readonly Guid GradeLarge = Guid.NewGuid();
    private static readonly Guid GradeMedium = Guid.NewGuid();

    // ---- #396 quality-condition snapshots -------------------------------
    //
    // An official entry records WHICH grade its Cracked and Dirty counters
    // resolved to, so a later catalog change cannot reinterpret a past day.
    // The domain does not resolve them — it has no catalog — so the caller
    // passes the ids it resolved (both Active and IsSaleable, or null).

    private static readonly Guid GradeCracked = Guid.NewGuid();
    private static readonly Guid GradeDirty = Guid.NewGuid();

    [Fact]
    public void Draft_has_no_quality_snapshots()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 10, 5, 0, 0, [new GradeQuantity(GradeLarge, 85)]);

        Assert.Null(entry.CrackedGradeId);
        Assert.Null(entry.DirtyGradeId);
    }

    [Fact]
    public void Submit_records_the_resolved_quality_grades()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 10, 5, 0, 0, [new GradeQuantity(GradeLarge, 85)]);

        Assert.True(entry.Submit(GradeCracked, GradeDirty).IsSuccess);

        Assert.Equal(GradeCracked, entry.CrackedGradeId);
        Assert.Equal(GradeDirty, entry.DirtyGradeId);
    }

    [Fact]
    public void Submit_records_null_for_a_condition_that_is_a_loss()
    {
        // Non-saleable, or inactive: either way the caller resolved nothing,
        // and null is the durable record that this day's cracked eggs were a
        // loss — not an absence of information to be re-derived later.
        var entry = MakeDraft();
        entry.RecordProduction(100, 10, 5, 0, 0, [new GradeQuantity(GradeLarge, 85)]);

        entry.Submit(crackedGradeId: null, dirtyGradeId: GradeDirty);

        Assert.Null(entry.CrackedGradeId);
        Assert.Equal(GradeDirty, entry.DirtyGradeId);
    }

    [Fact]
    public void Adjust_keeps_the_original_snapshot_and_never_re_resolves()
    {
        // The load-bearing one. Submission is the ONLY resolution point: if an
        // adjustment re-resolved, a farm that turned Cracked non-saleable (or
        // deactivated it) between submit and correction would see a corrected
        // day silently stop counting eggs it had already banked as stock — or
        // the reverse, minting lots for a day that was recorded as a loss.
        var entry = MakeDraft();
        entry.RecordProduction(100, 10, 5, 0, 0, [new GradeQuantity(GradeLarge, 85)]);
        entry.Submit(GradeCracked, GradeDirty);

        var result = entry.ManagerAdjust(120, 10, 5, 0, 0, "recount", [new GradeQuantity(GradeLarge, 105)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(GradeCracked, entry.CrackedGradeId);
        Assert.Equal(GradeDirty, entry.DirtyGradeId);
    }

    [Fact]
    public void RecordProduction_OnDraft_Succeeds()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(1000, 10, 5, 3, 2);
        Assert.True(result.IsSuccess);
        Assert.Equal(1000, entry.TotalEggs);
        Assert.Equal(1, entry.Version);
    }

    [Fact]
    public void RecordProduction_OnLocked_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        entry.Lock(DateTimeOffset.UtcNow);
        var result = entry.RecordProduction(200, 0, 0, 0, 0);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
    }

    [Fact]
    public void Submit_AdvancesStateFromDraft()
    {
        var entry = MakeDraft();
        // 500 total − 5 cracked − 3 dirty − 1 discarded = 491 sellable, graded exactly.
        entry.RecordProduction(500, 5, 3, 1, 0, [new GradeQuantity(GradeLarge, 491)]);
        var result = entry.Submit();
        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
    }

    [Fact]
    public void Submit_WhenAlreadySubmitted_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        var result = entry.Submit();
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Lock_AfterSubmit_Succeeds()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        var result = entry.Lock(DateTimeOffset.UtcNow);
        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Locked, entry.Status);
    }

    [Fact]
    public void Submit_BumpsVersion()
    {
        // Version is the optimistic concurrency token: without a bump on submit,
        // two racing submits both pass the WHERE Version = N predicate and
        // duplicate the generated egg lots.
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        var before = entry.Version;
        entry.Submit();
        Assert.Equal(before + 1, entry.Version);
    }

    [Fact]
    public void RecordProduction_OnSubmitted_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        var result = entry.RecordProduction(200, 0, 0, 0, 0);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
    }

    [Fact]
    public void RecordProduction_WithGrades_StoresLines()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(1000, 10, 5, 3, 2,
            [new GradeQuantity(GradeLarge, 600), new GradeQuantity(GradeMedium, 300)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, entry.Grades.Count);
        Assert.Equal(600, entry.Grades.Single(g => g.EggGradeId == GradeLarge).Quantity);
        Assert.All(entry.Grades, g => Assert.Equal(entry.AccountId, g.AccountId));
    }

    // #394 — the control: a draft may be partially graded (or not graded at
    // all — see RecordProduction_OnDraft_Succeeds above) and still save. Only
    // Submit/ManagerAdjust require exact reconciliation; saving a draft never
    // did and still doesn't.
    [Fact]
    public void RecordProduction_PartialGrades_OnDraft_Succeeds()
    {
        var entry = MakeDraft();
        // 200 sellable (no losses); only 120 graded so far.
        var result = entry.RecordProduction(200, 0, 0, 0, 0,
            [new GradeQuantity(GradeLarge, 120)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Draft, entry.Status);
        Assert.Equal(120, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void RecordProduction_ReRecord_ReplacesGradeLines()
    {
        var entry = MakeDraft();
        entry.RecordProduction(1000, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 600)]);
        entry.RecordProduction(900, 0, 0, 0, 0, [new GradeQuantity(GradeMedium, 500)]);

        var line = Assert.Single(entry.Grades);
        Assert.Equal(GradeMedium, line.EggGradeId);
        Assert.Equal(500, line.Quantity);
    }

    [Fact]
    public void RecordProduction_OmittedGrades_PreservesLines()
    {
        var entry = MakeDraft();
        entry.RecordProduction(1000, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 600)]);

        // Older client re-records counts without the grades field.
        var result = entry.RecordProduction(950, 10, 5, 3, 1);

        Assert.True(result.IsSuccess);
        var line = Assert.Single(entry.Grades);
        Assert.Equal(600, line.Quantity);
    }

    [Fact]
    public void RecordProduction_OmittedGrades_StillValidatedAgainstNewTotals()
    {
        var entry = MakeDraft();
        entry.RecordProduction(1000, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 600)]);

        // New totals leave only 500 sellable — the preserved 600 no longer fits.
        var result = entry.RecordProduction(500, 0, 0, 0, 0);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesExceedTotal", result.Error.Code);
        Assert.Equal(1000, entry.TotalEggs);
    }

    [Fact]
    public void RecordProduction_EmptyGrades_ClearsLines()
    {
        var entry = MakeDraft();
        entry.RecordProduction(1000, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 600)]);

        var result = entry.RecordProduction(1000, 0, 0, 0, 0, []);

        Assert.True(result.IsSuccess);
        Assert.Empty(entry.Grades);
    }

    [Fact]
    public void RecordProduction_GradesExceedingSellable_Fails()
    {
        var entry = MakeDraft();
        // 100 total - 10 cracked - 5 dirty - 3 discarded = 82 sellable.
        var result = entry.RecordProduction(100, 10, 5, 3, 0,
            [new GradeQuantity(GradeLarge, 83)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesExceedTotal", result.Error.Code);
        Assert.Empty(entry.Grades);
    }

    [Fact]
    public void RecordProduction_DuplicateGrade_Fails()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(1000, 0, 0, 0, 0,
            [new GradeQuantity(GradeLarge, 100), new GradeQuantity(GradeLarge, 100)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.DuplicateGrade", result.Error.Code);
    }

    [Fact]
    public void RecordProduction_WithGrades_OnLocked_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        entry.Lock(DateTimeOffset.UtcNow);

        var result = entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 60)]);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
        Assert.Equal(100, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void ManagerAdjust_OnLocked_ReplacesGrades()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        entry.Lock(DateTimeOffset.UtcNow);

        var result = entry.ManagerAdjust(150, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 150)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal(150, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void Lock_StampsLockedAt()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        var at = new DateTimeOffset(2026, 7, 19, 6, 0, 0, TimeSpan.Zero);

        Assert.True(entry.Lock(at).IsSuccess);
        Assert.Equal(at, entry.LockedAtUtc);
    }

    // #69 — one adjust path for Submitted AND Locked (spec §8.1 MVP
    // simplification); each adjust snapshots what it replaced.
    [Fact]
    public void ManagerAdjust_OnSubmitted_Succeeds_AndSnapshotsPreviousValues()
    {
        var entry = MakeDraft();
        // 100 total − 5 cracked = 95 sellable, graded exactly.
        entry.RecordProduction(100, 5, 0, 0, 2, [new GradeQuantity(GradeLarge, 95)]);
        entry.Submit();

        // 90 total − 5 cracked = 85 sellable, graded exactly.
        var result = entry.ManagerAdjust(90, 5, 0, 0, 3, "  recount  ",
            [new GradeQuantity(GradeLarge, 85)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal("recount", entry.AdjustReason);
        Assert.Contains("\"totalEggs\":100", entry.AdjustedFromJson);
        Assert.Contains("\"quantity\":95", entry.AdjustedFromJson);
    }

    [Fact]
    public void ManagerAdjust_OnAdjusted_SucceedsAgain()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        entry.ManagerAdjust(90, 0, 0, 0, 0, "first", [new GradeQuantity(GradeLarge, 90)]);

        var result = entry.ManagerAdjust(95, 0, 0, 0, 0, "second", [new GradeQuantity(GradeLarge, 95)]);
        Assert.True(result.IsSuccess);
        Assert.Equal("second", entry.AdjustReason);
        Assert.Contains("\"totalEggs\":90", entry.AdjustedFromJson);
    }

    [Fact]
    public void ManagerAdjust_OnDraft_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
        var result = entry.ManagerAdjust(90, 0, 0, 0, 0, "nope");
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.NotAdjustable", result.Error.Code);
    }

    [Fact]
    public void ManagerAdjust_BlankReason_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        var result = entry.ManagerAdjust(90, 0, 0, 0, 0, "   ");
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.ReasonRequired", result.Error.Code);
    }

    [Fact]
    public void Void_FromSubmittedLockedAdjusted_Succeeds_FromDraftAndVoided_Fails()
    {
        var submitted = MakeDraft();
        submitted.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        submitted.Submit();
        Assert.True(submitted.Void("wrong flock").IsSuccess);
        Assert.Equal(DailyEntryStatus.Voided, submitted.Status);
        Assert.Equal("wrong flock", submitted.VoidReason);

        // Already voided → refused.
        Assert.Equal("DailyEntry.NotVoidable", submitted.Void("again").Error.Code);

        var draft = MakeDraft();
        draft.RecordProduction(100, 0, 0, 0, 0);
        Assert.Equal("DailyEntry.NotVoidable", draft.Void("nope").Error.Code);
    }

    [Fact]
    public void Void_BumpsVersion()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();
        var before = entry.Version;
        entry.Void("mistake");
        Assert.Equal(before + 1, entry.Version);
    }

    // ------------------------------------------------------------------
    // #394 — submit requires grade lines to total EXACTLY the sellable count
    // (totalEggs − cracked − dirty − discarded); ManagerAdjust has no draft
    // state of its own, so it is held to the same rule. The four cases named
    // in the issue: no grades, partial grades, exact reconciliation, and a
    // zero-sellable day.
    // ------------------------------------------------------------------

    [Fact]
    public void Submit_NoGrades_NonZeroSellable_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(200, 0, 0, 0, 0); // no grades at all
        var result = entry.Submit();

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
        Assert.Equal(DailyEntryStatus.Draft, entry.Status);
    }

    [Fact]
    public void Submit_PartialGrades_Fails()
    {
        var entry = MakeDraft();
        // 200 sellable; only 150 graded.
        entry.RecordProduction(200, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 150)]);
        var result = entry.Submit();

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
        Assert.Equal(DailyEntryStatus.Draft, entry.Status);
    }

    // Submit() itself can never actually observe an over-graded state: the
    // lenient check in RecordProduction (the only way to get grades onto a
    // Draft) already refuses grades summing past sellable before they are
    // ever stored, so gradedTotal <= sellable always holds by the time
    // Submit() reads it. ManagerAdjust has no such upstream gate — its
    // `grades` argument goes straight to the exact check — so it is the one
    // that actually exercises the "over" branch of that check.
    [Fact]
    public void ManagerAdjust_GradesOverSellable_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        // 150 sellable; 160 graded — over is refused too, not just under.
        var result = entry.ManagerAdjust(150, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 160)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status); // refused, unchanged
    }

    [Fact]
    public void Submit_ExactReconciliation_Succeeds()
    {
        var entry = MakeDraft();
        // 200 total − 10 cracked − 5 dirty − 3 discarded = 182 sellable.
        entry.RecordProduction(200, 10, 5, 3, 0,
            [new GradeQuantity(GradeLarge, 100), new GradeQuantity(GradeMedium, 82)]);
        var result = entry.Submit();

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
    }

    [Fact]
    public void Submit_ZeroSellable_NoGrades_Succeeds()
    {
        var entry = MakeDraft();
        // Every egg accounted for as loss — zero sellable reconciles to zero lines.
        entry.RecordProduction(50, 20, 20, 10, 0);
        var result = entry.Submit();

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
        Assert.Empty(entry.Grades);
    }

    [Fact]
    public void ManagerAdjust_NoGrades_NonZeroSellable_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        // Explicitly clears the grade lines; 150 sellable now reconciles to none.
        var result = entry.ManagerAdjust(150, 0, 0, 0, 0, "recount", []);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
        Assert.Equal(100, entry.Grades.Single().Quantity); // untouched on refusal
    }

    [Fact]
    public void ManagerAdjust_PartialGrades_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        var result = entry.ManagerAdjust(150, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 120)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
    }

    [Fact]
    public void ManagerAdjust_ExactReconciliation_Succeeds()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        var result = entry.ManagerAdjust(150, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 150)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(150, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void ManagerAdjust_ZeroSellable_NoGrades_Succeeds()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        // All 50 eggs now cracked — zero sellable, so clearing the lines reconciles.
        var result = entry.ManagerAdjust(50, 50, 0, 0, 0, "all cracked on recount", []);

        Assert.True(result.IsSuccess);
        Assert.Empty(entry.Grades);
    }

    // The control on the adjust side: omitting `grades` keeps the entry's
    // CURRENT lines, so an adjust that doesn't touch grading must still
    // reconcile them against the new totals — proving the check reads the
    // effective grades, not just an explicitly-passed list.
    [Fact]
    public void ManagerAdjust_OmittedGrades_StillValidatedAgainstNewTotals()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 100)]);
        entry.Submit();

        // Grades omitted (not touched) but the total changes — the carried-over
        // 100 no longer reconciles against a new sellable of 90.
        var result = entry.ManagerAdjust(90, 0, 0, 0, 0, "shrank without regrading");

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesNotReconciled", result.Error.Code);
    }
}
