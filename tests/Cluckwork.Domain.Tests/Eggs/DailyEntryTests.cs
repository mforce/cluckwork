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
}
