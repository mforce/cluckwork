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
}
