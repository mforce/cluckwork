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

    [Fact]
    public void RecordProduction_WithGrades_StoresLines()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(1000, 10, 5, 3, 2,
            [new GradeQuantity("A-Large", 600), new GradeQuantity("A-Medium", 300)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, entry.Grades.Count);
        Assert.Equal(600, entry.Grades.Single(g => g.GradeCode == "A-Large").Quantity);
        Assert.All(entry.Grades, g => Assert.Equal(entry.AccountId, g.AccountId));
    }

    [Fact]
    public void RecordProduction_ReRecord_ReplacesGradeLines()
    {
        var entry = MakeDraft();
        entry.RecordProduction(1000, 0, 0, 0, 0, [new GradeQuantity("A-Large", 600)]);
        entry.RecordProduction(900, 0, 0, 0, 0, [new GradeQuantity("A-Medium", 500)]);

        var line = Assert.Single(entry.Grades);
        Assert.Equal("A-Medium", line.GradeCode);
        Assert.Equal(500, line.Quantity);
    }

    [Fact]
    public void RecordProduction_GradesExceedingTotal_Fails()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(100, 0, 0, 0, 0,
            [new GradeQuantity("A-Large", 101)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.GradesExceedTotal", result.Error.Code);
        Assert.Empty(entry.Grades);
    }

    [Fact]
    public void RecordProduction_DuplicateGradeCode_Fails()
    {
        var entry = MakeDraft();
        var result = entry.RecordProduction(1000, 0, 0, 0, 0,
            [new GradeQuantity("A-Large", 100), new GradeQuantity("a-large", 100)]);

        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.DuplicateGrade", result.Error.Code);
    }

    [Fact]
    public void RecordProduction_WithGrades_OnLocked_Fails()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity("A-Large", 50)]);
        entry.Submit();
        entry.Lock();

        var result = entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity("A-Large", 60)]);
        Assert.True(result.IsFailure);
        Assert.Equal("DailyEntry.Immutable", result.Error.Code);
        Assert.Equal(50, entry.Grades.Single().Quantity);
    }

    [Fact]
    public void ManagerAdjust_OnLocked_ReplacesGrades()
    {
        var entry = MakeDraft();
        entry.RecordProduction(100, 0, 0, 0, 0, [new GradeQuantity("A-Large", 50)]);
        entry.Submit();
        entry.Lock();

        var result = entry.ManagerAdjust(120, 0, 0, 0, 0, "recount",
            [new GradeQuantity("A-Large", 70)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(DailyEntryStatus.ManagerAdjusted, entry.Status);
        Assert.Equal(70, entry.Grades.Single().Quantity);
    }
}
