namespace Cluckwork.Application.Tests.DailyEntries;

using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

public sealed class RecordDailyEntryValidatorTests
{
    private readonly RecordDailyEntryValidator _validator = new();

    private static RecordDailyEntryCommand Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateOnly.FromDateTime(DateTime.Today),
        TotalEggs: 1000, CrackedEggs: 10, DirtyEggs: 5, DiscardedEggs: 3, MortalityCount: 2);

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.Validate(Valid());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmptyFarmId_FailsValidation()
    {
        var cmd = Valid() with { FarmId = Guid.Empty };
        var result = _validator.Validate(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RecordDailyEntryCommand.FarmId));
    }

    [Fact]
    public void NegativeEggs_FailsValidation()
    {
        var cmd = Valid() with { TotalEggs = -1 };
        var result = _validator.Validate(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RecordDailyEntryCommand.TotalEggs));
    }

    [Fact]
    public void CrackedPlusDirtyExceedsTotal_FailsWithEggsError()
    {
        var cmd = Valid() with { TotalEggs = 100, CrackedEggs = 60, DirtyEggs = 60 };
        var result = _validator.Validate(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == "Eggs");
    }

    [Fact]
    public void ValidGrades_Pass()
    {
        var cmd = Valid() with
        {
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 600), new GradeQuantityDto(Guid.NewGuid(), 300)]
        };
        Assert.True(_validator.Validate(cmd).IsValid);
    }

    [Fact]
    public void NoGrades_StillValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void GradeWithNonPositiveQuantity_Fails()
    {
        var cmd = Valid() with { Grades = [new GradeQuantityDto(Guid.NewGuid(), 0)] };
        Assert.False(_validator.Validate(cmd).IsValid);
    }

    [Fact]
    public void DuplicateGradeIds_Fail()
    {
        var gradeId = Guid.NewGuid();
        var cmd = Valid() with
        {
            Grades = [new GradeQuantityDto(gradeId, 100), new GradeQuantityDto(gradeId, 50)]
        };
        var result = _validator.Validate(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public void GradesSumExceedingTotal_Fails()
    {
        var cmd = Valid() with
        {
            TotalEggs = 100,
            CrackedEggs = 0, DirtyEggs = 0, DiscardedEggs = 0,
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 101)]
        };
        var result = _validator.Validate(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public void GradesSumExceedingSellable_Fails()
    {
        // 100 total - 10 cracked - 5 dirty - 3 discarded = 82 sellable.
        var cmd = Valid() with
        {
            TotalEggs = 100, CrackedEggs = 10, DirtyEggs = 5, DiscardedEggs = 3,
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 83)]
        };
        var result = _validator.Validate(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public void HugeGradeQuantities_FailValidation_InsteadOfOverflowing()
    {
        var cmd = Valid() with
        {
            Grades =
            [
                new GradeQuantityDto(Guid.NewGuid(), 1_500_000_000),
                new GradeQuantityDto(Guid.NewGuid(), 1_500_000_000)
            ]
        };
        var result = _validator.Validate(cmd);
        Assert.False(result.IsValid);
    }
}
