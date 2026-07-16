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
        entry.Lock();
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
        var result = entry.Lock();
        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.Locked, entry.Status);
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
        entry.Lock();

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
        entry.Lock();

        var result = entry.ManagerAdjust(120, 0, 0, 0, 0, "recount",
            [new GradeQuantity(GradeLarge, 70)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal(70, entry.Grades.Single().Quantity);
    }
}
