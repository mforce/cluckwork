namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

public sealed class DailyEntryTests
{
    private static DailyEntry MakeDraft() =>
        DailyEntry.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today));

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
        entry.RecordProduction(100, 0, 0, 0, 0);
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
        entry.RecordProduction(500, 5, 3, 1, 0);
        var result = entry.Submit();
        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Submitted, entry.Status);
    }

    [Fact]
    public void Submit_WhenAlreadySubmitted_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
        entry.Submit();
        var result = entry.Submit();
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Lock_AfterSubmit_Succeeds()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
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
        entry.RecordProduction(100, 0, 0, 0, 0);
        var before = entry.Version;
        entry.Submit();
        Assert.Equal(before + 1, entry.Version);
    }

    [Fact]
    public void RecordProduction_OnSubmitted_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
        entry.Submit();

        var result = entry.RecordProduction(200, 0, 0, 0, 0);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
    }

    private static readonly Guid GradeLarge = Guid.NewGuid();
    private static readonly Guid GradeMedium = Guid.NewGuid();

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
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 50)]);
        entry.Submit();
        entry.Lock(DateTimeOffset.UtcNow);

        var result = entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 60)]);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
        Assert.Equal(50, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void ManagerAdjust_OnLocked_ReplacesGrades()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity(GradeLarge, 50)]);
        entry.Submit();
        entry.Lock(DateTimeOffset.UtcNow);

        var result = entry.ManagerAdjust(120, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 70)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal(70, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void Lock_StampsLockedAt()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
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
        entry.RecordProduction(100, 5, 0, 0, 2, [new GradeQuantity(GradeLarge, 50)]);
        entry.Submit();

        var result = entry.ManagerAdjust(90, 5, 0, 0, 3, "  recount  ",
            [new GradeQuantity(GradeLarge, 40)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal("recount", entry.AdjustReason);
        Assert.Contains("\"totalEggs\":100", entry.AdjustedFromJson);
        Assert.Contains("\"quantity\":50", entry.AdjustedFromJson);
    }

    [Fact]
    public void ManagerAdjust_OnAdjusted_SucceedsAgain()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0);
        entry.Submit();
        entry.ManagerAdjust(90, 0, 0, 0, 0, "first");

        var result = entry.ManagerAdjust(95, 0, 0, 0, 0, "second");
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
        entry.RecordProduction(100, 0, 0, 0, 0);
        entry.Submit();
        var result = entry.ManagerAdjust(90, 0, 0, 0, 0, "   ");
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.ReasonRequired", result.Error.Code);
    }

    [Fact]
    public void Void_FromSubmittedLockedAdjusted_Succeeds_FromDraftAndVoided_Fails()
    {
        var submitted = MakeDraft();
        submitted.RecordProduction(100, 0, 0, 0, 0);
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
        entry.RecordProduction(100, 0, 0, 0, 0);
        entry.Submit();
        var before = entry.Version;
        entry.Void("mistake");
        Assert.Equal(before + 1, entry.Version);
    }
}
