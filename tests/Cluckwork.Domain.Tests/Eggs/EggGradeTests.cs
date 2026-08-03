namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

// #396 — a Daily Entry's Cracked and Dirty counters feed a specific grade, and
// which grade that is must survive the farm renaming it. `DailyEntryKind` is
// that stable binding: an identity the code can resolve, as opposed to matching
// on the mutable `Name` (a farm renaming "Cracked" to "Segunda" would otherwise
// silently detach its own counter from its own grade).
public sealed class EggGradeTests
{
    private static EggGrade Make(
        string name = "Large",
        EggGradeType type = EggGradeType.Size,
        bool saleable = true,
        DailyEntryKind kind = DailyEntryKind.Manual) =>
        EggGrade.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name,
            type, sortOrder: 0, isSaleable: saleable, dailyEntryKind: kind);

    [Fact]
    public void Create_defaults_to_the_manual_kind()
    {
        // Every ordinary grade is graded by hand in the Grading pane. Only the
        // two condition grades are fed by a counter, so Manual is the default
        // and a condition kind is always a deliberate act.
        Assert.Equal(DailyEntryKind.Manual, Make().DailyEntryKind);
    }

    [Theory]
    [InlineData(DailyEntryKind.Cracked)]
    [InlineData(DailyEntryKind.Dirty)]
    public void Create_carries_a_condition_kind(DailyEntryKind kind) =>
        Assert.Equal(kind, Make("Cracked", EggGradeType.Quality, kind: kind).DailyEntryKind);

    [Fact]
    public void Update_cannot_change_the_kind()
    {
        // Same reasoning as GradeType's immutability, and stronger: an entry
        // that already snapshotted this grade as its Cracked identity would be
        // reinterpreted by a later kind change. Update takes no kind parameter
        // at all — this pins that the signature cannot quietly grow one.
        var grade = Make("Cracked", EggGradeType.Quality, kind: DailyEntryKind.Cracked);

        var result = grade.Update("Segunda", sortOrder: 3, isSaleable: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("Segunda", grade.Name);
        Assert.Equal(DailyEntryKind.Cracked, grade.DailyEntryKind);
    }

    [Fact]
    public void Deactivate_leaves_saleability_alone()
    {
        // The trap #397's review round 3 caught, pinned at the source: the two
        // flags move independently, so "inactive but still saleable" is an
        // ordinary reachable state. Every consumer resolving a condition grade
        // must therefore require BOTH, and this test is what makes that
        // requirement visible rather than folklore.
        var grade = Make("Cracked", EggGradeType.Quality, kind: DailyEntryKind.Cracked);

        Assert.True(grade.Deactivate().IsSuccess);

        Assert.False(grade.Active);
        Assert.True(grade.IsSaleable);
    }
}
