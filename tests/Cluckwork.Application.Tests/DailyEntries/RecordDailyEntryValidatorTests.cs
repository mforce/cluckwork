namespace Cluckwork.Application.Tests.DailyEntries;

using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Tests.Common;

public sealed class RecordDailyEntryValidatorTests
{
    // A fixed farm-local today, so the date rule is deterministic instead of
    // riding on the machine's clock (#35). Shared with the other validator
    // tests (#155) so "today" means the same date across all of them.
    private static readonly DateOnly FarmToday = FixedFarmClock.Today;

    private readonly RecordDailyEntryValidator _validator = new(FixedFarmClock.AtDefault());

    private static RecordDailyEntryCommand Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        FarmToday,
        TotalEggs: 1000, CrackedEggs: 10, DirtyEggs: 5, DiscardedEggs: 3, MortalityCount: 2);

    [Fact]
    public async Task ValidCommand_PassesValidation()
    {
        var result = await _validator.ValidateAsync(Valid());
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task EmptyFarmId_FailsValidation()
    {
        var cmd = Valid() with { FarmId = Guid.Empty };
        var result = await _validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RecordDailyEntryCommand.FarmId));
    }

    [Fact]
    public async Task NegativeEggs_FailsValidation()
    {
        var cmd = Valid() with { TotalEggs = -1 };
        var result = await _validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == nameof(RecordDailyEntryCommand.TotalEggs));
    }

    [Fact]
    public async Task CrackedPlusDirtyExceedsTotal_FailsWithEggsError()
    {
        var cmd = Valid() with { TotalEggs = 100, CrackedEggs = 60, DirtyEggs = 60 };
        var result = await _validator.ValidateAsync(cmd);

        Assert.Contains(result.Errors, error => error.PropertyName == "Eggs");
    }

    [Fact]
    public async Task FarFutureDate_Fails()
    {
        var cmd = Valid() with { Date = FarmToday.AddDays(3) };
        var result = await _validator.ValidateAsync(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordDailyEntryCommand.Date));
    }

    [Fact]
    public async Task FarmLocalToday_Passes()
    {
        var result = await _validator.ValidateAsync(Valid() with { Date = FarmToday });
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task DayAfterFarmLocalToday_Fails()
    {
        // The old rule allowed UTC-today + 1 as slack for farms ahead of UTC.
        // The boundary is now the farm's own today, so tomorrow is rejected
        // outright — a farm BEHIND UTC could previously post a real future day.
        var cmd = Valid() with { Date = FarmToday.AddDays(1) };
        var result = await _validator.ValidateAsync(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordDailyEntryCommand.Date));
    }

    [Fact]
    public async Task PastDate_StillPasses()
    {
        var result = await _validator.ValidateAsync(Valid() with { Date = FarmToday.AddDays(-30) });
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidGrades_Pass()
    {
        var cmd = Valid() with
        {
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 600), new GradeQuantityDto(Guid.NewGuid(), 300)]
        };
        Assert.True((await _validator.ValidateAsync(cmd)).IsValid);
    }

    [Fact]
    public async Task NoGrades_StillValid()
    {
        Assert.True((await _validator.ValidateAsync(Valid())).IsValid);
    }

    [Fact]
    public async Task GradeWithNonPositiveQuantity_Fails()
    {
        var cmd = Valid() with { Grades = [new GradeQuantityDto(Guid.NewGuid(), 0)] };
        Assert.False((await _validator.ValidateAsync(cmd)).IsValid);
    }

    [Fact]
    public async Task DuplicateGradeIds_Fail()
    {
        var gradeId = Guid.NewGuid();
        var cmd = Valid() with
        {
            Grades = [new GradeQuantityDto(gradeId, 100), new GradeQuantityDto(gradeId, 50)]
        };
        var result = await _validator.ValidateAsync(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public async Task GradesSumExceedingTotal_Fails()
    {
        var cmd = Valid() with
        {
            TotalEggs = 100,
            CrackedEggs = 0, DirtyEggs = 0, DiscardedEggs = 0,
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 101)]
        };
        var result = await _validator.ValidateAsync(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public async Task GradesSumExceedingSellable_Fails()
    {
        // 100 total - 10 cracked - 5 dirty - 3 discarded = 82 sellable.
        var cmd = Valid() with
        {
            TotalEggs = 100, CrackedEggs = 10, DirtyEggs = 5, DiscardedEggs = 3,
            Grades = [new GradeQuantityDto(Guid.NewGuid(), 83)]
        };
        var result = await _validator.ValidateAsync(cmd);
        Assert.Contains(result.Errors, e => e.PropertyName == "Grades");
    }

    [Fact]
    public async Task HugeGradeQuantities_FailValidation_InsteadOfOverflowing()
    {
        var cmd = Valid() with
        {
            Grades =
            [
                new GradeQuantityDto(Guid.NewGuid(), 1_500_000_000),
                new GradeQuantityDto(Guid.NewGuid(), 1_500_000_000)
            ]
        };
        var result = await _validator.ValidateAsync(cmd);
        Assert.False(result.IsValid);
    }
}
